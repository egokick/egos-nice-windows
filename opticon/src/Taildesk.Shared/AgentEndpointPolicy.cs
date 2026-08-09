namespace Taildesk.Shared;

public static class AgentEndpointPolicy
{
    private const string InternalUpdateHealthPath = "/internal/update-health";
    private const string MediaPath = "/api/v1/media";
    private const string ExitNodePath = "/api/v1/actions/exit-node";

    public static bool IsInternalUpdateHealth(string? path) =>
        string.Equals(path, InternalUpdateHealthPath, StringComparison.OrdinalIgnoreCase);

    public static bool IsSignedMediaDownload(string? path) =>
        string.Equals(path, MediaPath, StringComparison.OrdinalIgnoreCase);

    public static bool RequiresPrimaryCommandCenter(string? path) =>
        IsSegment(path, "/api/v1/security")
        || IsSegment(path, "/api/v1/ssh")
        || IsSegment(path, "/api/v1/update")
        || string.Equals(path, ExitNodePath, StringComparison.OrdinalIgnoreCase);

    private static bool IsSegment(string? path, string segment)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.Equals(segment, StringComparison.OrdinalIgnoreCase)
               || (path.Length > segment.Length
                   && path.StartsWith(segment, StringComparison.OrdinalIgnoreCase)
                   && path[segment.Length] == '/');
    }
}
