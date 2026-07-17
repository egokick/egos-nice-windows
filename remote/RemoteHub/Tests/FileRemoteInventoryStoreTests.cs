using System.Security.Cryptography;
using StayActive.RemoteHub.Domain;
using StayActive.RemoteHub.Persistence;

namespace StayActive.RemoteHub.Tests;

public sealed class FileRemoteInventoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "stayactive-remotehub-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public async Task Upsert_replays_a_durable_hmac_chained_inventory_and_audit_log()
    {
        Directory.CreateDirectory(_directory);
        var journalPath = Path.Combine(_directory, "inventory.journal.jsonl");

        using (var store = CreateStore(journalPath))
        {
            var created = await store.UpsertAsync("node-42", CreateUpdate(expectedVersion: 0), "admin-a", "trace-a", CancellationToken.None);
            Assert.True(created.Succeeded);
            Assert.True(created.Created);
            Assert.Equal(1, created.Record!.Version);

            var updated = await store.UpsertAsync(
                "node-42",
                CreateUpdate(expectedVersion: 1, location: "Austin, TX", capabilities: [RemoteCapability.ExitNode]),
                "admin-b",
                "trace-b",
                CancellationToken.None);
            Assert.True(updated.Succeeded);
            Assert.False(updated.Created);
            Assert.Equal(2, updated.Record!.Version);
        }

        using var reopened = CreateStore(journalPath);
        var devices = await reopened.ListAsync(verifiedOnly: true, CancellationToken.None);
        var device = Assert.Single(devices);
        Assert.Equal("node-42", device.HeadscaleNodeId);
        Assert.Equal("Austin, TX", device.CoarseLocation);
        Assert.Equal(2, device.Version);
        Assert.Equal([RemoteCapability.ExitNode], device.AllowedCapabilities);

        var audit = await reopened.ReadAuditAsync(10, CancellationToken.None);
        Assert.Equal(2, audit.Count);
        Assert.Equal("inventory.updated", audit[0].EventType);
        Assert.Equal("inventory.created", audit[1].EventType);
        Assert.Equal("admin-b", audit[0].ActorSubject);
    }

    [Fact]
    public async Task Stale_version_never_changes_the_committed_journal()
    {
        Directory.CreateDirectory(_directory);
        var journalPath = Path.Combine(_directory, "inventory.journal.jsonl");
        using var store = CreateStore(journalPath);

        var created = await store.UpsertAsync("node-7", CreateUpdate(0), "admin", "trace-1", CancellationToken.None);
        Assert.True(created.Succeeded);

        var stale = await store.UpsertAsync("node-7", CreateUpdate(0), "admin", "trace-2", CancellationToken.None);
        Assert.False(stale.Succeeded);
        Assert.Equal(1, stale.CurrentVersion);

        var lines = await File.ReadAllLinesAsync(journalPath, CancellationToken.None);
        Assert.Single(lines);
        var audit = await store.ReadAuditAsync(10, CancellationToken.None);
        Assert.Single(audit);
    }

    [Fact]
    public async Task Tampered_journal_fails_closed_on_restart()
    {
        Directory.CreateDirectory(_directory);
        var journalPath = Path.Combine(_directory, "inventory.journal.jsonl");
        using (var store = CreateStore(journalPath))
        {
            var result = await store.UpsertAsync("node-99", CreateUpdate(0), "admin", "trace", CancellationToken.None);
            Assert.True(result.Succeeded);
        }

        var journal = await File.ReadAllTextAsync(journalPath, CancellationToken.None);
        await File.WriteAllTextAsync(journalPath, journal.Replace("node-99", "node-98", StringComparison.Ordinal), CancellationToken.None);

        var exception = Assert.Throws<InvalidDataException>(() => CreateStore(journalPath));
        Assert.Contains("journal", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Owner_data_without_explicit_opt_in_is_rejected()
    {
        var request = new InventoryUpdateRequest(
            ExpectedVersion: 0,
            OwnerDisplayNameOptIn: false,
            OwnerDisplayName: "not consented",
            CoarseLocationOptIn: false,
            CoarseLocation: null,
            MeshCentralNodeId: null,
            Verified: false,
            AllowedCapabilities: []);

        var valid = RemoteInventoryValidation.TryNormalizeUpdate(request, out _, out var errors);

        Assert.False(valid);
        Assert.Contains("ownerDisplayName", errors.Keys);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private FileRemoteInventoryStore CreateStore(string journalPath) => new(journalPath, _key);

    private static NormalizedInventoryUpdate CreateUpdate(
        long expectedVersion,
        string location = "Chicago, IL",
        RemoteCapability[]? capabilities = null)
    {
        var resolvedCapabilities = capabilities ?? [RemoteCapability.ExitNode, RemoteCapability.ScreenView];
        return new NormalizedInventoryUpdate(
            expectedVersion,
            OwnerDisplayNameOptIn: true,
            OwnerDisplayName: "Taylor",
            CoarseLocationOptIn: true,
            CoarseLocation: location,
            MeshCentralNodeId: resolvedCapabilities.Any(capability => capability is RemoteCapability.ScreenView or RemoteCapability.SendFile or RemoteCapability.RequestFile)
                ? "mesh-42"
                : null,
            Verified: true,
            AllowedCapabilities: resolvedCapabilities);
    }
}
