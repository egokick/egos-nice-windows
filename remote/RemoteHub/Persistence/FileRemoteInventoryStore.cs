using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StayActive.RemoteHub.Domain;

namespace StayActive.RemoteHub.Persistence;

/// <summary>
/// A small single-node event store. A durable, HMAC-chained append-only journal
/// is the sole source of truth: an update reaches memory only after its audit
/// record has been flushed to disk. Do not place this journal on a network file
/// share; deploy one RemoteHub writer backed up with the HMAC key separately.
/// </summary>
public sealed class FileRemoteInventoryStore : IRemoteInventoryStore, IDisposable
{
    private const int JournalSchemaVersion = 1;
    private const string CreatedEventType = "inventory.created";
    private const string UpdatedEventType = "inventory.updated";

    private readonly string _journalPath;
    private readonly string _leasePath;
    private readonly byte[] _hmacKey;
    private readonly TimeProvider _timeProvider;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<string, RemoteInventoryRecord> _inventory = new(StringComparer.Ordinal);
    private List<RemoteHubAuditEvent> _audit = [];
    private string _lastEntryHash = string.Empty;
    private bool _disposed;

    public FileRemoteInventoryStore(
        string journalPath,
        byte[] journalHmacKey,
        TimeProvider? timeProvider = null,
        JsonSerializerOptions? jsonOptions = null)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
        {
            throw new ArgumentException("A journal path is required.", nameof(journalPath));
        }

        if (journalHmacKey is null || journalHmacKey.Length < 32)
        {
            throw new ArgumentException("A journal HMAC key of at least 32 bytes is required.", nameof(journalHmacKey));
        }

        _journalPath = Path.GetFullPath(journalPath);
        _leasePath = _journalPath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        _hmacKey = journalHmacKey.ToArray();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jsonOptions = jsonOptions ?? RemoteHubJson.CreateOptions();
        ReplaceState(LoadJournal());
    }

    public async Task<IReadOnlyList<RemoteInventoryRecord>> ListAsync(bool verifiedOnly, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _inventory.Values
                .Where(record => !verifiedOnly || record.Verified)
                .OrderBy(static record => record.HeadscaleNodeId, StringComparer.Ordinal)
                .Select(RemoteInventoryValidation.Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InventoryUpsertResult> UpsertAsync(
        string headscaleNodeId,
        NormalizedInventoryUpdate update,
        string actorSubject,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(update);

        if (!RemoteInventoryValidation.TryNormalizeNodeId(headscaleNodeId, out var normalizedNodeId, out var nodeError))
        {
            throw new ArgumentException(nodeError, nameof(headscaleNodeId));
        }

        if (!RemoteHubAuthValue.IsSafe(actorSubject, 256))
        {
            throw new ArgumentException("Actor subject is invalid.", nameof(actorSubject));
        }

        if (!RemoteHubAuthValue.IsSafe(correlationId, 256))
        {
            throw new ArgumentException("Correlation ID is invalid.", nameof(correlationId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);

            // A second process is not a supported deployment topology, but this
            // reload makes version checks correct if an operator briefly runs a
            // replacement process against the same local volume.
            ReplaceState(LoadJournal());

            _inventory.TryGetValue(normalizedNodeId, out var existing);
            var expectedVersion = update.ExpectedVersion;
            if (existing is null ? expectedVersion != 0 : expectedVersion != existing.Version)
            {
                return InventoryUpsertResult.Conflict(existing?.Version);
            }

            var now = _timeProvider.GetUtcNow();
            var created = existing is null;
            var record = new RemoteInventoryRecord(
                normalizedNodeId,
                update.OwnerDisplayName,
                update.OwnerDisplayNameOptIn,
                update.CoarseLocation,
                update.CoarseLocationOptIn,
                update.MeshCentralNodeId,
                update.Verified,
                update.AllowedCapabilities.ToArray(),
                created ? 1 : existing!.Version + 1,
                now);

            if (!RemoteInventoryValidation.TryValidatePersistedRecord(record, out var recordError))
            {
                throw new InvalidOperationException($"Refusing to persist an invalid inventory record: {recordError}");
            }

            var audit = new RemoteHubAuditEvent(
                _audit.Count + 1L,
                Guid.NewGuid(),
                now,
                created ? CreatedEventType : UpdatedEventType,
                actorSubject,
                normalizedNodeId,
                existing?.Version ?? 0,
                record.Version,
                RemoteInventoryValidation.ComputeRecordDigest(record, _jsonOptions),
                correlationId);
            var entry = CreateSignedEntry(audit, record, _lastEntryHash);

            // The append is the commit point. If it throws, the old in-memory
            // state remains untouched and a restart observes the old journal.
            await AppendEntryAsync(entry, cancellationToken).ConfigureAwait(false);

            _inventory[normalizedNodeId] = record;
            _audit.Add(audit);
            _lastEntryHash = entry.EntryHash;
            return new InventoryUpsertResult(true, created, RemoteInventoryValidation.Clone(record), null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<RemoteHubAuditEvent>> ReadAuditAsync(int take, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (take is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "Audit take must be between 1 and 500.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _audit
                .OrderByDescending(static eventRecord => eventRecord.Sequence)
                .Take(take)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_hmacKey);
        _gate.Dispose();
    }

    private JournalEntry CreateSignedEntry(
        RemoteHubAuditEvent audit,
        RemoteInventoryRecord record,
        string previousEntryHash)
    {
        var payload = new JournalIntegrityPayload(JournalSchemaVersion, audit, record, previousEntryHash);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
        var entryHash = Convert.ToBase64String(HMACSHA256.HashData(_hmacKey, bytes));
        return new JournalEntry(JournalSchemaVersion, audit, record, previousEntryHash, entryHash);
    }

    private async Task AppendEntryAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(entry, _jsonOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await using var stream = new FileStream(
            _journalPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private async Task<FileStream> AcquireWriteLeaseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _leasePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private LoadedJournalState LoadJournal()
    {
        var inventory = new Dictionary<string, RemoteInventoryRecord>(StringComparer.Ordinal);
        var audit = new List<RemoteHubAuditEvent>();
        var previousEntryHash = string.Empty;

        if (!File.Exists(_journalPath))
        {
            return new LoadedJournalState(inventory, audit, previousEntryHash);
        }

        using var stream = new FileStream(_journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (line.Length == 0)
            {
                throw new InvalidDataException($"RemoteHub journal contains an empty entry at line {lineNumber}.");
            }

            JournalEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<JournalEntry>(line, _jsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"RemoteHub journal entry {lineNumber} is invalid JSON.", exception);
            }

            if (entry is null)
            {
                throw new InvalidDataException($"RemoteHub journal entry {lineNumber} is empty.");
            }

            ValidateJournalEntry(entry, previousEntryHash, inventory, audit, lineNumber);
            inventory[entry.Inventory.HeadscaleNodeId] = RemoteInventoryValidation.Clone(entry.Inventory);
            audit.Add(entry.Audit);
            previousEntryHash = entry.EntryHash;
        }

        return new LoadedJournalState(inventory, audit, previousEntryHash);
    }

    private void ValidateJournalEntry(
        JournalEntry entry,
        string expectedPreviousEntryHash,
        IReadOnlyDictionary<string, RemoteInventoryRecord> inventory,
        IReadOnlyCollection<RemoteHubAuditEvent> audit,
        int lineNumber)
    {
        if (entry.SchemaVersion != JournalSchemaVersion
            || !string.Equals(entry.PreviousEntryHash, expectedPreviousEntryHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"RemoteHub journal chain is invalid at line {lineNumber}.");
        }

        if (!RemoteInventoryValidation.TryValidatePersistedRecord(entry.Inventory, out var recordError))
        {
            throw new InvalidDataException($"RemoteHub journal has an invalid record at line {lineNumber}: {recordError}");
        }

        if (entry.Audit.Sequence != audit.Count + 1L
            || entry.Audit.EventId == Guid.Empty
            || entry.Audit.OccurredAtUtc == default
            || !RemoteHubAuthValue.IsSafe(entry.Audit.ActorSubject, 256)
            || !RemoteHubAuthValue.IsSafe(entry.Audit.CorrelationId, 256)
            || !string.Equals(entry.Audit.HeadscaleNodeId, entry.Inventory.HeadscaleNodeId, StringComparison.Ordinal)
            || entry.Audit.Version != entry.Inventory.Version
            || !string.Equals(entry.Audit.RecordDigest, RemoteInventoryValidation.ComputeRecordDigest(entry.Inventory, _jsonOptions), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"RemoteHub journal audit metadata is invalid at line {lineNumber}.");
        }

        inventory.TryGetValue(entry.Inventory.HeadscaleNodeId, out var priorRecord);
        var expectedEventType = priorRecord is null ? CreatedEventType : UpdatedEventType;
        var expectedPreviousVersion = priorRecord?.Version ?? 0;
        var expectedVersion = expectedPreviousVersion + 1;
        if (!string.Equals(entry.Audit.EventType, expectedEventType, StringComparison.Ordinal)
            || entry.Audit.PreviousVersion != expectedPreviousVersion
            || entry.Inventory.Version != expectedVersion)
        {
            throw new InvalidDataException($"RemoteHub journal record version chain is invalid at line {lineNumber}.");
        }

        var payload = new JournalIntegrityPayload(entry.SchemaVersion, entry.Audit, entry.Inventory, entry.PreviousEntryHash);
        var expectedEntryHash = HMACSHA256.HashData(_hmacKey, JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions));
        byte[] suppliedEntryHash;
        try
        {
            suppliedEntryHash = Convert.FromBase64String(entry.EntryHash);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"RemoteHub journal HMAC encoding is invalid at line {lineNumber}.", exception);
        }

        if (!CryptographicOperations.FixedTimeEquals(suppliedEntryHash, expectedEntryHash))
        {
            throw new InvalidDataException($"RemoteHub journal HMAC is invalid at line {lineNumber}.");
        }
    }

    private void ReplaceState(LoadedJournalState state)
    {
        _inventory = state.Inventory;
        _audit = state.Audit;
        _lastEntryHash = state.LastEntryHash;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record JournalEntry(
        int SchemaVersion,
        RemoteHubAuditEvent Audit,
        RemoteInventoryRecord Inventory,
        string PreviousEntryHash,
        string EntryHash);

    private sealed record JournalIntegrityPayload(
        int SchemaVersion,
        RemoteHubAuditEvent Audit,
        RemoteInventoryRecord Inventory,
        string PreviousEntryHash);

    private sealed record LoadedJournalState(
        Dictionary<string, RemoteInventoryRecord> Inventory,
        List<RemoteHubAuditEvent> Audit,
        string LastEntryHash);
}

internal static class RemoteHubAuthValue
{
    public static bool IsSafe(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength && value.All(static character => !char.IsControl(character));
}
