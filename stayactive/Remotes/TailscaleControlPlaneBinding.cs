using System.Diagnostics;
using System.Text.Json;

namespace StayActive.Remotes;

/// <summary>
/// Verifies that the installed Tailscale client is actually bound to the
/// configured self-hosted Headscale controller. Configuration alone is not a
/// trust signal: local status data is accepted only after the read-only client
/// preferences report the same canonical HTTPS controller URL.
/// </summary>
internal static class TailscaleControlPlaneBinding
{
    internal static ProcessStartInfo CreateDebugPreferencesStartInfo(string executable)
    {
        if (!Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException("A fully qualified Tailscale executable path is required.", nameof(executable));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Each token remains literal and no command shell is involved.
        startInfo.ArgumentList.Add("debug");
        startInfo.ArgumentList.Add("prefs");
        return startInfo;
    }

    internal static bool MatchesConfiguredControlPlane(string? preferencesJson, Uri configuredControlPlane)
    {
        ArgumentNullException.ThrowIfNull(configuredControlPlane);

        if (string.IsNullOrWhiteSpace(preferencesJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(preferencesJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetTopLevelProperty(root, "ControlURL", out var controlUrlValue)
                || controlUrlValue.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var reportedControlUrl = controlUrlValue.GetString();
            return TryCreateSelfHostedControlPlane(reportedControlUrl, out var reportedControlPlane)
                && CanonicallyEquals(configuredControlPlane, reportedControlPlane);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryCreateSelfHostedControlPlane(string? value, out Uri controlPlane)
    {
        controlPlane = null!;
        if (!RemoteClientPreferences.IsSelfHostedControlPlane(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var parsedControlPlane))
        {
            return false;
        }

        controlPlane = parsedControlPlane;
        return true;
    }

    private static bool CanonicallyEquals(Uri configured, Uri reported)
    {
        return string.Equals(configured.Scheme, reported.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                configured.IdnHost.TrimEnd('.'),
                reported.IdnHost.TrimEnd('.'),
                StringComparison.OrdinalIgnoreCase)
            && EffectivePort(configured) == EffectivePort(reported)
            && string.Equals(CanonicalPath(configured), CanonicalPath(reported), StringComparison.Ordinal);
    }

    private static int EffectivePort(Uri uri)
    {
        return uri.IsDefaultPort ? 443 : uri.Port;
    }

    private static string CanonicalPath(Uri uri)
    {
        var path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        path = "/" + path.TrimStart('/');
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static bool TryGetTopLevelProperty(
        JsonElement root,
        string propertyName,
        out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
