namespace YouTubeSyncTray;

internal static class LocalBrowserHost
{
    internal const string DefaultValue = "tom.localhost";
    internal const string EnvironmentVariableName = "YOUTUBE_SYNC_BROWSER_HOST";

    public static string Resolve()
    {
        return Normalize(Environment.GetEnvironmentVariable(EnvironmentVariableName));
    }

    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultValue;
        }

        var candidate = value.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absoluteUri)
            && (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(absoluteUri.Host))
        {
            candidate = absoluteUri.Host;
        }
        else
        {
            candidate = candidate.TrimEnd('/');
        }

        return Uri.CheckHostName(candidate) == UriHostNameType.Unknown
            ? DefaultValue
            : candidate.ToLowerInvariant();
    }
}
