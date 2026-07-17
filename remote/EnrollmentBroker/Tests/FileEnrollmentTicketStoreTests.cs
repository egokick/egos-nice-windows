using System.Security.Cryptography;
using StayActive.EnrollmentBroker.Domain;
using StayActive.EnrollmentBroker.Persistence;

namespace StayActive.EnrollmentBroker.Tests;

public sealed class FileEnrollmentTicketStoreTests
{
    [Fact]
    public async Task Durable_journal_replays_terminal_state_and_never_contains_a_raw_pre_auth_key()
    {
        var directory = CreateDirectory();
        var journalPath = Path.Combine(directory, "tickets.journal.jsonl");
        var hmacKey = RandomNumberGenerator.GetBytes(32);
        const string rawKey = "hskey-auth-must-not-reach-the-broker-journal";
        try
        {
            var createdAt = DateTimeOffset.UtcNow;
            var ticket = CreateTicket(Guid.NewGuid(), "42", createdAt);
            using (var store = new FileEnrollmentTicketStore(journalPath, hmacKey))
            {
                await store.CreateAsync(ticket, "operator-a", "correlation-a", CancellationToken.None);
                var transition = await store.TransitionAsync(
                    ticket.Id,
                    EnrollmentTicketStatus.Revoked,
                    "operator-a",
                    "correlation-b",
                    CancellationToken.None);
                Assert.True(transition.Found);
                Assert.True(transition.Applied);
                Assert.Equal(EnrollmentTicketStatus.Revoked, transition.Ticket!.Status);
            }

            using (var reopened = new FileEnrollmentTicketStore(journalPath, hmacKey))
            {
                var loaded = await reopened.GetAsync(ticket.Id, CancellationToken.None);
                Assert.NotNull(loaded);
                Assert.Equal(EnrollmentTicketStatus.Revoked, loaded!.Status);
                var audit = await reopened.ReadAuditAsync(10, CancellationToken.None);
                Assert.Equal(["ticket.revoked", "ticket.issued"], audit.Select(entry => entry.EventType).ToArray());
            }

            var journal = await File.ReadAllTextAsync(journalPath);
            Assert.DoesNotContain(rawKey, journal, StringComparison.Ordinal);
            Assert.DoesNotContain("authKey", journal, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rawKey", journal, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hmacKey);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Journal_tampering_or_duplicate_headscale_key_tracking_fails_closed()
    {
        var directory = CreateDirectory();
        var journalPath = Path.Combine(directory, "tickets.journal.jsonl");
        var hmacKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var createdAt = DateTimeOffset.UtcNow;
            var first = CreateTicket(Guid.NewGuid(), "42", createdAt);
            using (var store = new FileEnrollmentTicketStore(journalPath, hmacKey))
            {
                await store.CreateAsync(first, "operator-a", "correlation-a", CancellationToken.None);
                var duplicateKey = CreateTicket(Guid.NewGuid(), "42", createdAt.AddSeconds(1));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    store.CreateAsync(duplicateKey, "operator-a", "correlation-b", CancellationToken.None));
            }

            var journal = await File.ReadAllTextAsync(journalPath);
            await File.WriteAllTextAsync(journalPath, journal.Replace("ticket.issued", "ticket.forged", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => new FileEnrollmentTicketStore(journalPath, hmacKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hmacKey);
            DeleteDirectory(directory);
        }
    }

    private static EnrollmentTicket CreateTicket(Guid id, string headscaleKeyId, DateTimeOffset createdAt) => new(
        id,
        EnrollmentTicketKind.Device,
        [EnrollmentTicketPolicy.DeviceTag],
        headscaleKeyId,
        createdAt,
        createdAt.AddMinutes(15),
        EnrollmentTicketStatus.Issued,
        "operator-a",
        createdAt);

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stayactive-enrollmentbroker-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
