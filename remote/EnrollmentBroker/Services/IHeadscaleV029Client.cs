using StayActive.EnrollmentBroker.Domain;

namespace StayActive.EnrollmentBroker.Services;

/// <summary>
/// Narrow wrapper around the Headscale 0.29 REST admin API.  It has no generic
/// request method, which keeps the broker unable to create users, alter ACLs,
/// approve routes, or administer arbitrary Headscale resources.
/// </summary>
public interface IHeadscaleV029Client
{
    Task<HeadscaleCreatedPreAuthKey> CreateOneUsePreAuthKeyAsync(
        string userId,
        DateTimeOffset expirationUtc,
        IReadOnlyList<string> aclTags,
        CancellationToken cancellationToken);

    Task<HeadscalePreAuthKeyStatus?> GetPreAuthKeyStatusAsync(
        string keyId,
        CancellationToken cancellationToken);

    Task ExpirePreAuthKeyAsync(string keyId, CancellationToken cancellationToken);
}

/// <summary>
/// This is the only type that contains the raw key.  It is created from the
/// Headscale creation response, handed directly to the first HTTP response,
/// and never accepted by persistence or logging code.
/// </summary>
public sealed class HeadscaleCreatedPreAuthKey
{
    internal HeadscaleCreatedPreAuthKey(
        string id,
        string rawKey,
        bool reusable,
        bool ephemeral,
        bool used,
        DateTimeOffset? expirationUtc,
        string[] aclTags)
    {
        Id = id;
        RawKey = rawKey;
        Reusable = reusable;
        Ephemeral = ephemeral;
        Used = used;
        ExpirationUtc = expirationUtc;
        AclTags = aclTags;
    }

    public string Id { get; }

    internal string RawKey { get; }

    public bool Reusable { get; }

    public bool Ephemeral { get; }

    public bool Used { get; }

    public DateTimeOffset? ExpirationUtc { get; }

    public string[] AclTags { get; }

    public override string ToString() =>
        $"HeadscaleCreatedPreAuthKey {{ Id = {Id}, RawKey = [REDACTED], Reusable = {Reusable}, Ephemeral = {Ephemeral}, Used = {Used} }}";
}

public sealed record HeadscalePreAuthKeyStatus(
    string Id,
    bool Reusable,
    bool Ephemeral,
    bool Used,
    DateTimeOffset? ExpirationUtc,
    string[] AclTags);

public sealed class HeadscaleApiException : Exception
{
    public HeadscaleApiException(string operation, int statusCode)
        : base($"Headscale {operation} returned HTTP {statusCode}.")
    {
        Operation = operation;
        StatusCode = statusCode;
    }

    public string Operation { get; }

    public int StatusCode { get; }
}

public sealed class HeadscaleProtocolException : Exception
{
    public HeadscaleProtocolException(string message)
        : base(message)
    {
    }
}

public sealed class HeadscaleUnavailableException : Exception
{
    public HeadscaleUnavailableException()
        : base("Headscale is unavailable.")
    {
    }
}
