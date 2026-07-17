using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StayActive.EnrollmentBroker.Domain;

namespace StayActive.EnrollmentBroker.Persistence;

/// <summary>
/// A deliberately small durable store for enrollment metadata.  The raw
/// Headscale pre-authentication key is never accepted by this type, so it
/// cannot reach disk through a ticket or audit record.  Every append includes
/// the prior entry HMAC, making tampering and dropped/reordered lines fail
/// closed during startup or the next operation.
/// </summary>
public sealed class FileEnrollmentTicketStore : IEnrollmentTicketStore
{
    private const int JournalSchemaVersion = 1;
    private const string IssuedEventType = "ticket.issued";
    private const string RedeemedEventType = "ticket.redeemed";
    private const string RevokedEventType = "ticket.revoked";
    private const string ExpiredEventType = "ticket.expired";

    private readonly string _journalPath;
    private readonly string _leasePath;
    private readonly byte[] _hmacKey;
    private readonly TimeProvider _timeProvider;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<Guid, EnrollmentTicket> _tickets = [];
    private List<EnrollmentTicketAuditEvent> _audit = [];
    private string _lastEntryHash = string.Empty;
    private bool _disposed;

    public FileEnrollmentTicketStore(
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
        var directory = Path.GetDirectoryName(_journalPath)
            ?? throw new ArgumentException("The journal path must contain a directory.", nameof(journalPath));
        Directory.CreateDirectory(directory);
        _leasePath = _journalPath + ".lock";
        _hmacKey = journalHmacKey.ToArray();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jsonOptions = jsonOptions ?? EnrollmentBrokerJson.CreateOptions();

        ReplaceState(LoadJournal());
    }

    public async Task<EnrollmentTicket?> GetAsync(Guid ticketId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
            ReplaceState(LoadJournal());
            return _tickets.TryGetValue(ticketId, out var ticket) ? Clone(ticket) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CreateAsync(
        EnrollmentTicket ticket,
        string actorSubject,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!EnrollmentTicketPolicy.TryValidatePersisted(ticket, out var ticketError))
        {
            throw new ArgumentException($"Refusing to persist an invalid enrollment ticket: {ticketError}", nameof(ticket));
        }

        if (ticket.Status != EnrollmentTicketStatus.Issued
            || ticket.StatusChangedAtUtc != ticket.CreatedAtUtc
            || !string.Equals(ticket.IssuedBy, actorSubject, StringComparison.Ordinal)
            || !EnrollmentBrokerValue.IsSafe(actorSubject, 256)
            || !EnrollmentBrokerValue.IsSafe(correlationId, 256))
        {
            throw new ArgumentException("The enrollment ticket creation metadata is invalid.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
            ReplaceState(LoadJournal());
            if (_tickets.ContainsKey(ticket.Id))
            {
                throw new InvalidOperationException("An enrollment ticket with this id already exists.");
            }

            if (_tickets.Values.Any(existing => string.Equals(
                    existing.HeadscaleKeyId,
                    ticket.HeadscaleKeyId,
                    StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("A Headscale pre-authentication key is already tracked by another enrollment ticket.");
            }

            var audit = CreateAudit(ticket, IssuedEventType, actorSubject, correlationId);
            var entry = CreateSignedEntry(audit, ticket, _lastEntryHash);
            await AppendEntryAsync(entry, cancellationToken).ConfigureAwait(false);

            _tickets[ticket.Id] = Clone(ticket);
            _audit.Add(audit);
            _lastEntryHash = entry.EntryHash;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EnrollmentTicketTransitionResult> TransitionAsync(
        Guid ticketId,
        EnrollmentTicketStatus status,
        string actorSubject,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (status == EnrollmentTicketStatus.Issued || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (!EnrollmentBrokerValue.IsSafe(actorSubject, 256)
            || !EnrollmentBrokerValue.IsSafe(correlationId, 256))
        {
            throw new ArgumentException("The enrollment ticket transition metadata is invalid.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
            ReplaceState(LoadJournal());
            if (!_tickets.TryGetValue(ticketId, out var existing))
            {
                return EnrollmentTicketTransitionResult.Missing;
            }

            if (existing.Status != EnrollmentTicketStatus.Issued)
            {
                return new EnrollmentTicketTransitionResult(true, false, Clone(existing));
            }

            var now = _timeProvider.GetUtcNow();
            if (now < existing.CreatedAtUtc)
            {
                throw new InvalidOperationException("The configured time provider moved backwards.");
            }

            var updated = existing with
            {
                Status = status,
                StatusChangedAtUtc = now
            };
            if (!EnrollmentTicketPolicy.TryValidatePersisted(updated, out var ticketError))
            {
                throw new InvalidOperationException($"Refusing to persist an invalid enrollment transition: {ticketError}");
            }

            var audit = CreateAudit(updated, EventNameFor(status), actorSubject, correlationId);
            var entry = CreateSignedEntry(audit, updated, _lastEntryHash);
            await AppendEntryAsync(entry, cancellationToken).ConfigureAwait(false);

            _tickets[ticketId] = Clone(updated);
            _audit.Add(audit);
            _lastEntryHash = entry.EntryHash;
            return new EnrollmentTicketTransitionResult(true, true, Clone(updated));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<EnrollmentTicketAuditEvent>> ReadAuditAsync(int take, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (take is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "Audit take must be between 1 and 500.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease = await AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
            ReplaceState(LoadJournal());
            return _audit
                .OrderByDescending(static audit => audit.Sequence)
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

    private EnrollmentTicketAuditEvent CreateAudit(
        EnrollmentTicket ticket,
        string eventType,
        string actorSubject,
        string correlationId) =>
        new(
            _audit.Count + 1L,
            Guid.NewGuid(),
            _timeProvider.GetUtcNow(),
            eventType,
            actorSubject,
            ticket.Id,
            EnrollmentTicketPolicy.ComputeTicketDigest(ticket, _jsonOptions),
            correlationId);

    private JournalEntry CreateSignedEntry(
        EnrollmentTicketAuditEvent audit,
        EnrollmentTicket ticket,
        string previousEntryHash)
    {
        var payload = new JournalIntegrityPayload(JournalSchemaVersion, audit, ticket, previousEntryHash);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
        var entryHash = Convert.ToBase64String(HMACSHA256.HashData(_hmacKey, bytes));
        return new JournalEntry(JournalSchemaVersion, audit, ticket, previousEntryHash, entryHash);
    }

    private async Task AppendEntryAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, _jsonOptions) + "\n");
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
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private LoadedJournalState LoadJournal()
    {
        var tickets = new Dictionary<Guid, EnrollmentTicket>();
        var audit = new List<EnrollmentTicketAuditEvent>();
        var previousEntryHash = string.Empty;
        if (!File.Exists(_journalPath))
        {
            return new LoadedJournalState(tickets, audit, previousEntryHash);
        }

        using var stream = new FileStream(_journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (line.Length == 0)
            {
                throw new InvalidDataException($"Enrollment journal contains an empty entry at line {lineNumber}.");
            }

            JournalEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<JournalEntry>(line, _jsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Enrollment journal entry {lineNumber} is invalid JSON.", exception);
            }

            if (entry is null)
            {
                throw new InvalidDataException($"Enrollment journal entry {lineNumber} is empty.");
            }

            ValidateJournalEntry(entry, previousEntryHash, tickets, audit, lineNumber);
            tickets[entry.Ticket.Id] = Clone(entry.Ticket);
            audit.Add(entry.Audit);
            previousEntryHash = entry.EntryHash;
        }

        return new LoadedJournalState(tickets, audit, previousEntryHash);
    }

    private void ValidateJournalEntry(
        JournalEntry entry,
        string expectedPreviousEntryHash,
        IReadOnlyDictionary<Guid, EnrollmentTicket> tickets,
        IReadOnlyCollection<EnrollmentTicketAuditEvent> audit,
        int lineNumber)
    {
        var ticketValid = EnrollmentTicketPolicy.TryValidatePersisted(entry.Ticket, out var ticketError);
        if (entry.SchemaVersion != JournalSchemaVersion
            || !string.Equals(entry.PreviousEntryHash, expectedPreviousEntryHash, StringComparison.Ordinal)
            || !ticketValid)
        {
            throw new InvalidDataException($"Enrollment journal record is invalid at line {lineNumber}: {ticketError ?? "integrity chain mismatch"}.");
        }

        if (entry.Audit.Sequence != audit.Count + 1L
            || entry.Audit.EventId == Guid.Empty
            || entry.Audit.OccurredAtUtc == default
            || entry.Audit.OccurredAtUtc.Offset != TimeSpan.Zero
            || !EnrollmentBrokerValue.IsSafe(entry.Audit.EventType, 64)
            || !EnrollmentBrokerValue.IsSafe(entry.Audit.ActorSubject, 256)
            || !EnrollmentBrokerValue.IsSafe(entry.Audit.CorrelationId, 256)
            || entry.Audit.TicketId != entry.Ticket.Id
            || !string.Equals(
                entry.Audit.TicketDigest,
                EnrollmentTicketPolicy.ComputeTicketDigest(entry.Ticket, _jsonOptions),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Enrollment journal audit metadata is invalid at line {lineNumber}.");
        }

        tickets.TryGetValue(entry.Ticket.Id, out var prior);
        if (prior is null)
        {
            if (entry.Ticket.Status != EnrollmentTicketStatus.Issued
                || entry.Ticket.StatusChangedAtUtc != entry.Ticket.CreatedAtUtc
                || !string.Equals(entry.Audit.EventType, IssuedEventType, StringComparison.Ordinal)
                || !string.Equals(entry.Audit.ActorSubject, entry.Ticket.IssuedBy, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Enrollment ticket creation is invalid at line {lineNumber}.");
            }
        }
        else
        {
            if (prior.Status != EnrollmentTicketStatus.Issued
                || entry.Ticket.Status == EnrollmentTicketStatus.Issued
                || !SameTicketExceptStatus(prior, entry.Ticket)
                || entry.Ticket.StatusChangedAtUtc < prior.StatusChangedAtUtc
                || !string.Equals(entry.Audit.EventType, EventNameFor(entry.Ticket.Status), StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Enrollment ticket transition is invalid at line {lineNumber}.");
            }
        }

        var payload = new JournalIntegrityPayload(entry.SchemaVersion, entry.Audit, entry.Ticket, entry.PreviousEntryHash);
        var expectedEntryHash = HMACSHA256.HashData(_hmacKey, JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions));
        byte[] suppliedEntryHash;
        try
        {
            suppliedEntryHash = Convert.FromBase64String(entry.EntryHash);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"Enrollment journal HMAC encoding is invalid at line {lineNumber}.", exception);
        }

        if (!CryptographicOperations.FixedTimeEquals(suppliedEntryHash, expectedEntryHash))
        {
            throw new InvalidDataException($"Enrollment journal HMAC is invalid at line {lineNumber}.");
        }
    }

    private static bool SameTicketExceptStatus(EnrollmentTicket prior, EnrollmentTicket next) =>
        prior.Id == next.Id
        && prior.Kind == next.Kind
        && prior.Tags.SequenceEqual(next.Tags, StringComparer.Ordinal)
        && string.Equals(prior.HeadscaleKeyId, next.HeadscaleKeyId, StringComparison.Ordinal)
        && prior.CreatedAtUtc == next.CreatedAtUtc
        && prior.ExpiresAtUtc == next.ExpiresAtUtc
        && string.Equals(prior.IssuedBy, next.IssuedBy, StringComparison.Ordinal);

    private static string EventNameFor(EnrollmentTicketStatus status) => status switch
    {
        EnrollmentTicketStatus.Redeemed => RedeemedEventType,
        EnrollmentTicketStatus.Revoked => RevokedEventType,
        EnrollmentTicketStatus.Expired => ExpiredEventType,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private void ReplaceState(LoadedJournalState state)
    {
        _tickets = state.Tickets;
        _audit = state.Audit;
        _lastEntryHash = state.LastEntryHash;
    }

    private static EnrollmentTicket Clone(EnrollmentTicket ticket) => ticket with { Tags = ticket.Tags.ToArray() };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record JournalEntry(
        int SchemaVersion,
        EnrollmentTicketAuditEvent Audit,
        EnrollmentTicket Ticket,
        string PreviousEntryHash,
        string EntryHash);

    private sealed record JournalIntegrityPayload(
        int SchemaVersion,
        EnrollmentTicketAuditEvent Audit,
        EnrollmentTicket Ticket,
        string PreviousEntryHash);

    private sealed record LoadedJournalState(
        Dictionary<Guid, EnrollmentTicket> Tickets,
        List<EnrollmentTicketAuditEvent> Audit,
        string LastEntryHash);
}
