using System.Text.Json;

namespace YouTubeSyncTray;

internal sealed class CookieExportMetadataStore
{
    private readonly YoutubeSyncPaths _paths;

    public CookieExportMetadataStore(YoutubeSyncPaths paths)
    {
        _paths = paths;
    }

    public bool Matches(BrowserCookieSource browser, string profile)
    {
        var metadata = Load();
        if (!metadata.HasValue)
        {
            return false;
        }

        return metadata.Value.Browser == browser
            && string.Equals(metadata.Value.Profile, NormalizeProfile(profile), StringComparison.OrdinalIgnoreCase);
    }

    public void Save(BrowserCookieSource browser, string profile)
    {
        var metadata = new CookieExportMetadata(browser, NormalizeProfile(profile), DateTimeOffset.UtcNow);
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.CookiesMetadataPath)!);
        AtomicFile.WriteAllText(_paths.CookiesMetadataPath, JsonSerializer.Serialize(metadata));
    }

    public CookieExportMetadata? Load()
    {
        try
        {
            if (!AtomicFile.TryReadJson<CookieExportMetadata>(_paths.CookiesMetadataPath, out var metadata))
            {
                return null;
            }

            return metadata;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeProfile(string profile) =>
        string.IsNullOrWhiteSpace(profile) ? "Default" : profile.Trim();

    internal readonly record struct CookieExportMetadata(
        BrowserCookieSource Browser,
        string Profile,
        DateTimeOffset ExportedAtUtc);
}
