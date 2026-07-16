using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace StayActive.RemoteHub.Domain;

/// <summary>
/// Inventory capabilities only. RemoteHub deliberately has no endpoint that
/// performs any of these operations; another consent-enforcing component must
/// do that work.
/// </summary>
public enum RemoteCapability
{
    ExitNode = 1,
    ScreenView = 2,
    SendFile = 3,
    RequestFile = 4
}

/// <summary>
/// The versioned, operator-approved mapping consumed by the StayActive tray.
/// Names and locations may only be stored and returned when their corresponding
/// opt-in flag is true.
/// </summary>
public sealed record RemoteInventoryRecord(
    string HeadscaleNodeId,
    string? OwnerDisplayName,
    bool OwnerDisplayNameOptIn,
    string? CoarseLocation,
    bool CoarseLocationOptIn,
    string? MeshCentralNodeId,
    bool Verified,
    RemoteCapability[] AllowedCapabilities,
    long Version,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Body accepted by the restricted inventory-admin endpoint. Version zero only
/// creates a mapping; updates must name the current version to prevent writes
/// from silently overwriting one another.
/// </summary>
public sealed record InventoryUpdateRequest(
    long ExpectedVersion,
    bool OwnerDisplayNameOptIn,
    string? OwnerDisplayName,
    bool CoarseLocationOptIn,
    string? CoarseLocation,
    string? MeshCentralNodeId,
    bool Verified,
    RemoteCapability[]? AllowedCapabilities);

public sealed record NormalizedInventoryUpdate(
    long ExpectedVersion,
    bool OwnerDisplayNameOptIn,
    string? OwnerDisplayName,
    bool CoarseLocationOptIn,
    string? CoarseLocation,
    string? MeshCentralNodeId,
    bool Verified,
    RemoteCapability[] AllowedCapabilities);

public sealed record RemoteHubAuditEvent(
    long Sequence,
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    string EventType,
    string ActorSubject,
    string HeadscaleNodeId,
    long PreviousVersion,
    long Version,
    string RecordDigest,
    string CorrelationId);

public sealed record InventoryUpsertResult(
    bool Succeeded,
    bool Created,
    RemoteInventoryRecord? Record,
    long? CurrentVersion)
{
    public static InventoryUpsertResult Conflict(long? currentVersion) =>
        new(false, false, null, currentVersion);
}

public static partial class RemoteInventoryValidation
{
    private const int MaxNodeIdLength = 128;
    private const int MaxDisplayNameLength = 128;
    private const int MaxLocationLength = 160;
    private const int MaxMeshCentralNodeIdLength = 256;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex NodeIdPattern();

    public static bool TryNormalizeNodeId(string? value, out string nodeId, out string? error)
    {
        nodeId = Normalize(value);
        if (nodeId.Length == 0 || nodeId.Length > MaxNodeIdLength || !NodeIdPattern().IsMatch(nodeId))
        {
            error = "Headscale node ID must be 1-128 characters of letters, digits, '.', '_', ':', or '-'.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryNormalizeUpdate(
        InventoryUpdateRequest? request,
        out NormalizedInventoryUpdate? update,
        out IReadOnlyDictionary<string, string[]> errors)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        update = null;

        if (request is null)
        {
            result["body"] = ["A JSON inventory update body is required."];
            errors = result;
            return false;
        }

        if (request.ExpectedVersion < 0)
        {
            result["expectedVersion"] = ["Expected version must be zero or a positive integer."];
        }

        var owner = Normalize(request.OwnerDisplayName);
        if (request.OwnerDisplayNameOptIn)
        {
            if (!IsSafeText(owner, MaxDisplayNameLength))
            {
                result["ownerDisplayName"] = ["An opted-in owner display name is required and must be at most 128 printable characters."];
            }
        }
        else if (owner.Length != 0)
        {
            result["ownerDisplayName"] = ["Owner display name must be omitted unless the owner opted in."];
        }

        var location = Normalize(request.CoarseLocation);
        if (request.CoarseLocationOptIn)
        {
            if (!IsSafeText(location, MaxLocationLength))
            {
                result["coarseLocation"] = ["An opted-in coarse location is required and must be at most 160 printable characters."];
            }
        }
        else if (location.Length != 0)
        {
            result["coarseLocation"] = ["Coarse location must be omitted unless the owner opted in."];
        }

        var meshCentralNodeId = Normalize(request.MeshCentralNodeId);
        if (meshCentralNodeId.Length != 0 && !IsSafeText(meshCentralNodeId, MaxMeshCentralNodeIdLength))
        {
            result["meshCentralNodeId"] = ["MeshCentral node ID must be at most 256 printable characters."];
        }

        var capabilities = request.AllowedCapabilities ?? [];
        if (capabilities.Length > Enum.GetValues<RemoteCapability>().Length || capabilities.Any(value => !Enum.IsDefined(value)))
        {
            result["allowedCapabilities"] = ["Allowed capabilities contain an unknown value."];
        }

        var normalizedCapabilities = capabilities
            .Distinct()
            .OrderBy(static value => (int)value)
            .ToArray();

        if (normalizedCapabilities.Length != capabilities.Length)
        {
            result["allowedCapabilities"] = ["Allowed capabilities must not contain duplicates."];
        }

        if (normalizedCapabilities.Any(RequiresMeshCentralNodeId) && meshCentralNodeId.Length == 0)
        {
            result["meshCentralNodeId"] = ["A MeshCentral node ID is required for screen or file capabilities."];
        }

        if (result.Count != 0)
        {
            errors = result;
            return false;
        }

        update = new NormalizedInventoryUpdate(
            request.ExpectedVersion,
            request.OwnerDisplayNameOptIn,
            owner.Length == 0 ? null : owner,
            request.CoarseLocationOptIn,
            location.Length == 0 ? null : location,
            meshCentralNodeId.Length == 0 ? null : meshCentralNodeId,
            request.Verified,
            normalizedCapabilities);
        errors = result;
        return true;
    }

    public static bool TryValidatePersistedRecord(RemoteInventoryRecord? record, out string? error)
    {
        if (record is null)
        {
            error = "Persisted inventory record is missing.";
            return false;
        }

        if (!TryNormalizeNodeId(record.HeadscaleNodeId, out _, out error))
        {
            return false;
        }

        if (record.Version < 1 || record.UpdatedAtUtc == default)
        {
            error = "Persisted inventory version or timestamp is invalid.";
            return false;
        }

        var request = new InventoryUpdateRequest(
            record.Version,
            record.OwnerDisplayNameOptIn,
            record.OwnerDisplayName,
            record.CoarseLocationOptIn,
            record.CoarseLocation,
            record.MeshCentralNodeId,
            record.Verified,
            record.AllowedCapabilities);
        if (!TryNormalizeUpdate(request, out var normalized, out var errors))
        {
            error = string.Join(" ", errors.SelectMany(static pair => pair.Value));
            return false;
        }

        if (!string.Equals(record.OwnerDisplayName, normalized!.OwnerDisplayName, StringComparison.Ordinal)
            || !string.Equals(record.CoarseLocation, normalized.CoarseLocation, StringComparison.Ordinal)
            || !string.Equals(record.MeshCentralNodeId, normalized.MeshCentralNodeId, StringComparison.Ordinal)
            || !record.AllowedCapabilities.SequenceEqual(normalized.AllowedCapabilities))
        {
            error = "Persisted inventory record is not normalized.";
            return false;
        }

        error = null;
        return true;
    }

    public static RemoteInventoryRecord Clone(RemoteInventoryRecord record) =>
        record with { AllowedCapabilities = record.AllowedCapabilities.ToArray() };

    public static string ComputeRecordDigest(RemoteInventoryRecord record, JsonSerializerOptions serializerOptions)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(record, serializerOptions);
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private static bool RequiresMeshCentralNodeId(RemoteCapability capability) =>
        capability is RemoteCapability.ScreenView or RemoteCapability.SendFile or RemoteCapability.RequestFile;

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static bool IsSafeText(string value, int maximumLength) =>
        value.Length > 0 && value.Length <= maximumLength && value.All(static character => !char.IsControl(character));
}

public static class RemoteHubJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter<RemoteCapability>(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
