using System.Net;
using StayActive.EnrollmentBroker.Configuration;
using StayActive.EnrollmentBroker.Domain;
using StayActive.EnrollmentBroker.Persistence;

namespace StayActive.EnrollmentBroker.Services;

/// <summary>
/// Applies the enrollment policy around the narrow Headscale client.  It
/// treats an unavailable or surprising Headscale result as unsafe instead of
/// guessing that a key has been revoked.  The raw key is only returned from
/// <see cref="IssueAsync"/> after its non-secret ticket metadata is durable.
/// </summary>
public sealed class EnrollmentTicketService
{
    private static readonly TimeSpan HeadscaleExpirationTolerance = TimeSpan.FromSeconds(2);
    private const string HeadscaleSystemActor = "system:headscale";
    private const string ExpirySystemActor = "system:expiry";

    private readonly EnrollmentBrokerSettings _settings;
    private readonly IEnrollmentTicketStore _store;
    private readonly IHeadscaleV029Client _headscale;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EnrollmentTicketService> _logger;

    public EnrollmentTicketService(
        EnrollmentBrokerSettings settings,
        IEnrollmentTicketStore store,
        IHeadscaleV029Client headscale,
        TimeProvider timeProvider,
        ILogger<EnrollmentTicketService> logger)
    {
        _settings = settings;
        _store = store;
        _headscale = headscale;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<EnrollmentTicketCreateResponse> IssueAsync(
        EnrollmentTicketCreateRequest request,
        string actorSubject,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!EnrollmentTicketPolicy.TryCreate(request, out var kind, out var lifetimeMinutes, out var tags, out var errors))
        {
            throw new EnrollmentTicketValidationException(errors);
        }

        var now = _timeProvider.GetUtcNow();
        var expiration = now.AddMinutes(lifetimeMinutes);
        var createdKey = await _headscale.CreateOneUsePreAuthKeyAsync(
            _settings.HeadscaleUserId,
            expiration,
            tags,
            cancellationToken).ConfigureAwait(false);

        if (!IsValidCreatedKey(createdKey, expiration, tags))
        {
            await RevokeUnexpectedCreatedKeyAsync(createdKey.Id, CancellationToken.None).ConfigureAwait(false);
            throw new EnrollmentTicketUnsafeHeadscaleResponseException();
        }

        var ticket = new EnrollmentTicket(
            Guid.NewGuid(),
            kind,
            tags.ToArray(),
            createdKey.Id,
            now,
            expiration,
            EnrollmentTicketStatus.Issued,
            actorSubject,
            now);

        try
        {
            await _store.CreateAsync(ticket, actorSubject, correlationId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The raw key must never be re-issued after a persistence failure.
            // Try to eliminate the usable key; report only generic state to the
            // caller and log neither the key nor the exception text.
            await RevokeUnexpectedCreatedKeyAsync(createdKey.Id, CancellationToken.None).ConfigureAwait(false);
            _logger.LogCritical("Enrollment ticket persistence failed after a Headscale key was created; revocation was requested.");
            throw new EnrollmentTicketUnsafeStateException();
        }

        return new EnrollmentTicketCreateResponse(ToView(ticket), createdKey.RawKey);
    }

    public async Task<EnrollmentTicket?> GetForOwnerAsync(
        Guid ticketId,
        string actorSubject,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var ticket = await _store.GetAsync(ticketId, cancellationToken).ConfigureAwait(false);
        if (ticket is null || !IsOwnedBy(ticket, actorSubject))
        {
            return null;
        }

        return await RefreshStatusAsync(ticket, correlationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnrollmentTicketRevocationResult> RevokeForOwnerAsync(
        Guid ticketId,
        string actorSubject,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var ticket = await _store.GetAsync(ticketId, cancellationToken).ConfigureAwait(false);
        if (ticket is null || !IsOwnedBy(ticket, actorSubject))
        {
            return EnrollmentTicketRevocationResult.Missing;
        }

        if (ticket.Status != EnrollmentTicketStatus.Issued)
        {
            return new EnrollmentTicketRevocationResult(true, false, ticket);
        }

        var now = _timeProvider.GetUtcNow();
        HeadscalePreAuthKeyStatus? remoteStatus;
        try
        {
            remoteStatus = await _headscale.GetPreAuthKeyStatusAsync(ticket.HeadscaleKeyId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HeadscaleApiException)
        {
            throw new EnrollmentTicketUnsafeStateException();
        }

        if (remoteStatus is null)
        {
            return await TransitionAsync(ticket, EnrollmentTicketStatus.Revoked, HeadscaleSystemActor, correlationId, cancellationToken)
                .ConfigureAwait(false);
        }

        EnsureRemoteStatusIsExpected(ticket, remoteStatus);
        if (remoteStatus.Used)
        {
            var redeemed = await TransitionAsync(ticket, EnrollmentTicketStatus.Redeemed, HeadscaleSystemActor, correlationId, cancellationToken)
                .ConfigureAwait(false);
            return new EnrollmentTicketRevocationResult(true, false, redeemed.Ticket, AlreadyRedeemed: true);
        }

        try
        {
            await _headscale.ExpirePreAuthKeyAsync(ticket.HeadscaleKeyId, cancellationToken).ConfigureAwait(false);
        }
        catch (HeadscaleApiException exception) when (exception.StatusCode == (int)HttpStatusCode.NotFound)
        {
            return await TransitionAsync(ticket, EnrollmentTicketStatus.Revoked, HeadscaleSystemActor, correlationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HeadscaleApiException)
        {
            throw new EnrollmentTicketUnsafeStateException();
        }

        var terminalStatus = now >= ticket.ExpiresAtUtc
            ? EnrollmentTicketStatus.Expired
            : EnrollmentTicketStatus.Revoked;
        return await TransitionAsync(ticket, terminalStatus, actorSubject, correlationId, cancellationToken).ConfigureAwait(false);
    }

    public EnrollmentTicketView ToView(EnrollmentTicket ticket) => new(
        ticket.Id,
        EnrollmentTicketPolicy.ToWireValue(ticket.Kind),
        EnrollmentTicketPolicy.ToWireValue(ticket.Status),
        ticket.ExpiresAtUtc,
        _settings.HeadscaleLoginServer,
        ticket.Tags.ToArray());

    private async Task<EnrollmentTicket> RefreshStatusAsync(
        EnrollmentTicket ticket,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (ticket.Status != EnrollmentTicketStatus.Issued)
        {
            return ticket;
        }

        var now = _timeProvider.GetUtcNow();
        if (now >= ticket.ExpiresAtUtc)
        {
            return (await TransitionAsync(ticket, EnrollmentTicketStatus.Expired, ExpirySystemActor, correlationId, cancellationToken)
                .ConfigureAwait(false)).Ticket!;
        }

        HeadscalePreAuthKeyStatus? remoteStatus;
        try
        {
            remoteStatus = await _headscale.GetPreAuthKeyStatusAsync(ticket.HeadscaleKeyId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HeadscaleApiException)
        {
            throw new EnrollmentTicketUnsafeStateException();
        }

        if (remoteStatus is null)
        {
            return (await TransitionAsync(ticket, EnrollmentTicketStatus.Revoked, HeadscaleSystemActor, correlationId, cancellationToken)
                .ConfigureAwait(false)).Ticket!;
        }

        EnsureRemoteStatusIsExpected(ticket, remoteStatus);
        if (remoteStatus.Used)
        {
            return (await TransitionAsync(ticket, EnrollmentTicketStatus.Redeemed, HeadscaleSystemActor, correlationId, cancellationToken)
                .ConfigureAwait(false)).Ticket!;
        }

        if (remoteStatus.ExpirationUtc is { } remoteExpiration && remoteExpiration <= now)
        {
            return (await TransitionAsync(ticket, EnrollmentTicketStatus.Expired, HeadscaleSystemActor, correlationId, cancellationToken)
                .ConfigureAwait(false)).Ticket!;
        }

        return ticket;
    }

    private async Task<EnrollmentTicketRevocationResult> TransitionAsync(
        EnrollmentTicket ticket,
        EnrollmentTicketStatus status,
        string actorSubject,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var transition = await _store.TransitionAsync(ticket.Id, status, actorSubject, correlationId, cancellationToken)
            .ConfigureAwait(false);
        if (!transition.Found || transition.Ticket is null)
        {
            throw new EnrollmentTicketUnsafeStateException();
        }

        return new EnrollmentTicketRevocationResult(true, transition.Applied, transition.Ticket);
    }

    private static bool IsOwnedBy(EnrollmentTicket ticket, string actorSubject) =>
        string.Equals(ticket.IssuedBy, actorSubject, StringComparison.Ordinal);

    private static bool IsValidCreatedKey(
        HeadscaleCreatedPreAuthKey key,
        DateTimeOffset expectedExpiration,
        IReadOnlyList<string> expectedTags) =>
        key.Id.Length is > 0 and <= 32
        && key.Id.All(static character => character is >= '0' and <= '9')
        && key.RawKey.Length is > 0 and <= 1024
        && key.RawKey.StartsWith("hskey-auth-", StringComparison.Ordinal)
        && !key.RawKey.Any(char.IsWhiteSpace)
        && !key.Reusable
        && !key.Ephemeral
        && !key.Used
        && key.ExpirationUtc is { } returnedExpiration
        && returnedExpiration <= expectedExpiration
        && returnedExpiration >= expectedExpiration - HeadscaleExpirationTolerance
        && key.AclTags.SequenceEqual(expectedTags, StringComparer.Ordinal);

    private static void EnsureRemoteStatusIsExpected(EnrollmentTicket ticket, HeadscalePreAuthKeyStatus status)
    {
        if (!string.Equals(ticket.HeadscaleKeyId, status.Id, StringComparison.Ordinal)
            || status.Reusable
            || status.Ephemeral
            || !status.AclTags.SequenceEqual(ticket.Tags, StringComparer.Ordinal)
            || status.ExpirationUtc is not { } expiration
            || expiration > ticket.ExpiresAtUtc)
        {
            throw new EnrollmentTicketUnsafeHeadscaleResponseException();
        }
    }

    private async Task RevokeUnexpectedCreatedKeyAsync(string keyId, CancellationToken cancellationToken)
    {
        if (keyId.Length == 0 || !keyId.All(static character => character is >= '0' and <= '9'))
        {
            return;
        }

        try
        {
            await _headscale.ExpirePreAuthKeyAsync(keyId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A caller must never receive a key after this path.  Avoid
            // logging exception details because an upstream response may have
            // reflected sensitive material.
            _logger.LogCritical("Headscale key revocation request failed while handling an unsafe enrollment state.");
        }
    }
}

public sealed record EnrollmentTicketRevocationResult(
    bool Found,
    bool Revoked,
    EnrollmentTicket? Ticket,
    bool AlreadyRedeemed = false)
{
    public static EnrollmentTicketRevocationResult Missing { get; } = new(false, false, null);
}

public sealed class EnrollmentTicketValidationException : Exception
{
    public EnrollmentTicketValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("The enrollment ticket request is invalid.")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

public sealed class EnrollmentTicketUnsafeStateException : Exception
{
    public EnrollmentTicketUnsafeStateException()
        : base("The enrollment ticket state could not be verified safely.")
    {
    }
}

public sealed class EnrollmentTicketUnsafeHeadscaleResponseException : Exception
{
    public EnrollmentTicketUnsafeHeadscaleResponseException()
        : base("Headscale returned a response outside the broker's fixed enrollment policy.")
    {
    }
}
