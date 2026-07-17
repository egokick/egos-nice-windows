using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StayActive.EnrollmentBroker.Domain;

/// <summary>
/// The only enrollment roles that this broker is allowed to issue.  Tags are
/// deliberately selected server-side: a caller can never submit arbitrary ACL
/// tags, an owner, a reusable flag, or an ephemeral flag.
/// </summary>
public enum EnrollmentTicketKind
{
    Device,
    ExitNode
}

public enum EnrollmentTicketStatus
{
    Issued,
    Redeemed,
    Revoked,
    Expired
}

public sealed record EnrollmentTicket(
    Guid Id,
    EnrollmentTicketKind Kind,
    string[] Tags,
    string HeadscaleKeyId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    EnrollmentTicketStatus Status,
    string IssuedBy,
    DateTimeOffset StatusChangedAtUtc);

public sealed record EnrollmentTicketAuditEvent(
    long Sequence,
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    string EventType,
    string ActorSubject,
    Guid TicketId,
    string TicketDigest,
    string CorrelationId);

/// <summary>
/// External requests remain intentionally tiny.  In particular, neither tags
/// nor a Headscale user can be supplied by a browser or tray client.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnrollmentTicketCreateRequest(string? Kind, int? LifetimeMinutes);

public sealed record EnrollmentTicketView(
    Guid Id,
    string Kind,
    string Status,
    DateTimeOffset ExpiresAtUtc,
    string LoginServer,
    string[] AdvertiseTags);

/// <summary>
/// The one response shape permitted to carry a raw pre-authentication key.
/// Its <see cref="ToString"/> output is deliberately redacted so an accidental
/// diagnostic, assertion, or structured logging call cannot reveal the key.
/// </summary>
public sealed class EnrollmentTicketCreateResponse
{
    public EnrollmentTicketCreateResponse(EnrollmentTicketView ticket, string authKey)
    {
        Ticket = ticket ?? throw new ArgumentNullException(nameof(ticket));
        AuthKey = string.IsNullOrWhiteSpace(authKey)
            ? throw new ArgumentException("An auth key is required.", nameof(authKey))
            : authKey;
    }

    public EnrollmentTicketView Ticket { get; }

    public string AuthKey { get; }

    public override string ToString() => $"EnrollmentTicketCreateResponse {{ Ticket = {Ticket}, AuthKey = [REDACTED] }}";
}

public sealed record EnrollmentTicketResponse(EnrollmentTicketView Ticket);

public static class EnrollmentTicketPolicy
{
    public const int MinimumLifetimeMinutes = 5;
    public const int MaximumLifetimeMinutes = 30;
    public const string DeviceTag = "tag:stayactive";
    public const string ExitNodeTag = "tag:stayactive-exit";

    public static bool TryCreate(
        EnrollmentTicketCreateRequest? request,
        out EnrollmentTicketKind kind,
        out int lifetimeMinutes,
        out string[] tags,
        out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        kind = default;
        lifetimeMinutes = 0;
        tags = [];

        if (request is null)
        {
            errors["request"] = ["A request body is required."];
            return false;
        }

        switch (request.Kind)
        {
            case "device":
                kind = EnrollmentTicketKind.Device;
                tags = [DeviceTag];
                break;
            case "exitNode":
                kind = EnrollmentTicketKind.ExitNode;
                tags = [DeviceTag, ExitNodeTag];
                break;
            default:
                errors["kind"] = ["Kind must be exactly 'device' or 'exitNode'."];
                break;
        }

        if (request.LifetimeMinutes is not int requestedLifetime
            || requestedLifetime < MinimumLifetimeMinutes
            || requestedLifetime > MaximumLifetimeMinutes)
        {
            errors["lifetimeMinutes"] = [$"LifetimeMinutes must be between {MinimumLifetimeMinutes} and {MaximumLifetimeMinutes}."];
        }
        else
        {
            lifetimeMinutes = requestedLifetime;
        }

        return errors.Count == 0;
    }

    public static string ToWireValue(EnrollmentTicketKind kind) => kind switch
    {
        EnrollmentTicketKind.Device => "device",
        EnrollmentTicketKind.ExitNode => "exitNode",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string ToWireValue(EnrollmentTicketStatus status) => status switch
    {
        EnrollmentTicketStatus.Issued => "issued",
        EnrollmentTicketStatus.Redeemed => "redeemed",
        EnrollmentTicketStatus.Revoked => "revoked",
        EnrollmentTicketStatus.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static string[] TagsFor(EnrollmentTicketKind kind) => kind switch
    {
        EnrollmentTicketKind.Device => [DeviceTag],
        EnrollmentTicketKind.ExitNode => [DeviceTag, ExitNodeTag],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static bool IsFixedTagSet(EnrollmentTicketKind kind, IReadOnlyList<string>? tags) =>
        tags is not null && tags.SequenceEqual(TagsFor(kind), StringComparer.Ordinal);

    public static bool TryValidatePersisted(EnrollmentTicket? ticket, out string? error)
    {
        if (ticket is null)
        {
            error = "Ticket is required.";
            return false;
        }

        if (ticket.Id == Guid.Empty)
        {
            error = "Ticket id is invalid.";
            return false;
        }

        if (!Enum.IsDefined(ticket.Kind) || !IsFixedTagSet(ticket.Kind, ticket.Tags))
        {
            error = "Ticket kind or fixed tags are invalid.";
            return false;
        }

        if (ticket.HeadscaleKeyId.Length is 0 or > 32
            || !ticket.HeadscaleKeyId.All(static character => character is >= '0' and <= '9'))
        {
            error = "Headscale key id is invalid.";
            return false;
        }

        if (ticket.CreatedAtUtc.Offset != TimeSpan.Zero
            || ticket.ExpiresAtUtc.Offset != TimeSpan.Zero
            || ticket.StatusChangedAtUtc.Offset != TimeSpan.Zero
            || ticket.CreatedAtUtc == default
            || ticket.ExpiresAtUtc <= ticket.CreatedAtUtc
            || ticket.ExpiresAtUtc - ticket.CreatedAtUtc < TimeSpan.FromMinutes(MinimumLifetimeMinutes)
            || ticket.ExpiresAtUtc - ticket.CreatedAtUtc > TimeSpan.FromMinutes(MaximumLifetimeMinutes)
            || ticket.StatusChangedAtUtc < ticket.CreatedAtUtc)
        {
            error = "Ticket timestamps are invalid.";
            return false;
        }

        if (!Enum.IsDefined(ticket.Status) || !EnrollmentBrokerValue.IsSafe(ticket.IssuedBy, 256))
        {
            error = "Ticket status or issuer is invalid.";
            return false;
        }

        error = null;
        return true;
    }

    public static string ComputeTicketDigest(EnrollmentTicket ticket, JsonSerializerOptions jsonOptions)
    {
        var digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ticket, jsonOptions));
        return Convert.ToBase64String(digest);
    }
}

public static class EnrollmentBrokerJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}

public static class EnrollmentBrokerValue
{
    public static bool IsSafe(string? value, int maximumLength) =>
        value is { Length: > 0 }
        && value.Length <= maximumLength
        && value.All(static character => !char.IsControl(character));
}
