using System.Collections.Concurrent;
using System.Security.Cryptography;

public sealed class FinanceCredentialLeaseStore
{
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(20);
    private const int MaximumRedemptionsPerField = 4;

    private readonly ConcurrentDictionary<string, CredentialLease> _leases = new(StringComparer.Ordinal);
    private readonly IFinanceCredentialStore _credentialStore;

    public FinanceCredentialLeaseStore(IFinanceCredentialStore credentialStore)
    {
        _credentialStore = credentialStore;
    }

    public string Create(string accountId, string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("A finance account ID is required.", nameof(accountId));
        }

        if (_credentialStore.Read(accountId) is null)
        {
            throw new InvalidOperationException(
                $"Windows Credential Manager has no saved credential for account '{accountName}' ({accountId}).");
        }

        RemoveExpiredLeases();
        while (true)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var lease = new CredentialLease(accountId, DateTimeOffset.UtcNow.Add(LeaseLifetime));
            if (_leases.TryAdd(token, lease))
            {
                return token;
            }
        }
    }

    public bool TryRedeem(string? token, string accountId, string field, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(token)
            || string.IsNullOrWhiteSpace(accountId)
            || (field != "username" && field != "password"))
        {
            return false;
        }

        RemoveExpiredLeases();
        if (!_leases.TryGetValue(token, out var lease))
        {
            return false;
        }

        lock (lease.Sync)
        {
            if (lease.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _leases.TryRemove(token, out _);
                return false;
            }

            if (!string.Equals(lease.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var redemptionCount = field == "username"
                ? lease.UsernameRedemptions
                : lease.PasswordRedemptions;
            if (redemptionCount >= MaximumRedemptionsPerField)
            {
                return false;
            }

            var credential = _credentialStore.Read(lease.AccountId);
            if (credential is null)
            {
                _leases.TryRemove(token, out _);
                return false;
            }

            if (field == "username")
            {
                lease.UsernameRedemptions++;
                value = credential.Username;
            }
            else
            {
                lease.PasswordRedemptions++;
                value = credential.Password;
            }

            return true;
        }
    }

    public void Revoke(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            _leases.TryRemove(token, out _);
        }
    }

    private void RemoveExpiredLeases()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _leases)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _leases.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class CredentialLease(string accountId, DateTimeOffset expiresAt)
    {
        public object Sync { get; } = new();
        public string AccountId { get; } = accountId;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public int UsernameRedemptions { get; set; }
        public int PasswordRedemptions { get; set; }
    }
}
