using System.Diagnostics;

namespace StayActive.Remotes;

internal enum RemoteWebAction
{
    ViewScreen,
    SendFile,
    RequestFile
}

internal sealed record RemoteActionAvailability(bool IsAvailable, string Reason)
{
    public static RemoteActionAvailability Available { get; } = new(true, string.Empty);
}

internal sealed record RemoteActionResult(bool Succeeded, string Message)
{
    public static RemoteActionResult Success(string message) => new(true, message);

    public static RemoteActionResult Failure(string message) => new(false, message);
}

internal interface IRemoteActionService
{
    RemoteActionAvailability GetAvailability(RemoteDevice device, RemoteWebAction action);

    RemoteActionResult Open(RemoteDevice device, RemoteWebAction action);
}

internal interface IExternalUriLauncher
{
    void Open(Uri uri);
}

/// <summary>
/// Starts only the locally configured, self-hosted MeshCentral device views.
/// Authentication, authorization, consent prompts, and file permissions stay
/// at the MeshCentral server; this client never receives a server login key.
/// </summary>
internal sealed class MeshCentralRemoteActionService : IRemoteActionService
{
    private readonly Func<RemoteClientPreferences> _getPreferences;
    private readonly IExternalUriLauncher _uriLauncher;

    public MeshCentralRemoteActionService(
        Func<RemoteClientPreferences> getPreferences,
        IExternalUriLauncher? uriLauncher = null)
    {
        _getPreferences = getPreferences;
        _uriLauncher = uriLauncher ?? new ShellExternalUriLauncher();
    }

    public RemoteActionAvailability GetAvailability(RemoteDevice device, RemoteWebAction action)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!device.IsVerified)
        {
            return new RemoteActionAvailability(false, "Verify this computer before starting a remote session.");
        }

        if (!device.IsOnline)
        {
            return new RemoteActionAvailability(false, "This computer is offline.");
        }

        if (!HasCapability(device, action))
        {
            return new RemoteActionAvailability(false, "This action is not authorized for this computer.");
        }

        if (!TryGetMeshCentralBaseUri(_getPreferences().MeshCentralUrl, out _))
        {
            return new RemoteActionAvailability(false, "Configure an HTTPS URL for your self-hosted MeshCentral server.");
        }

        if (!IsValidNodeId(device.MeshCentralNodeId))
        {
            return new RemoteActionAvailability(false, "This computer has not been linked to a MeshCentral device.");
        }

        return RemoteActionAvailability.Available;
    }

    public RemoteActionResult Open(RemoteDevice device, RemoteWebAction action)
    {
        var availability = GetAvailability(device, action);
        if (!availability.IsAvailable)
        {
            return RemoteActionResult.Failure(availability.Reason);
        }

        if (!TryGetMeshCentralBaseUri(_getPreferences().MeshCentralUrl, out var baseUri))
        {
            return RemoteActionResult.Failure("The configured MeshCentral URL is not a safe HTTPS endpoint.");
        }

        try
        {
            _uriLauncher.Open(BuildDeviceViewUri(baseUri, device.MeshCentralNodeId!, action));
            return RemoteActionResult.Success(action switch
            {
                RemoteWebAction.ViewScreen => "Opening the self-hosted screen session. The target's consent policy still applies.",
                RemoteWebAction.SendFile => "Opening the self-hosted file workspace to send a file.",
                _ => "Opening the self-hosted file workspace to request a file. The target chooses and approves any file."
            });
        }
        catch (Exception ex)
        {
            return RemoteActionResult.Failure($"Could not open the self-hosted remote session: {ex.Message}");
        }
    }

    internal static Uri BuildDeviceViewUri(Uri baseUri, string nodeId, RemoteWebAction action)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        var builder = new UriBuilder(baseUri)
        {
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/",
            Query = $"node={Uri.EscapeDataString(nodeId)}&viewmode={GetViewMode(action)}",
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private static bool HasCapability(RemoteDevice device, RemoteWebAction action)
    {
        return action switch
        {
            RemoteWebAction.ViewScreen => device.Capabilities.HasFlag(RemoteCapability.ScreenView),
            RemoteWebAction.SendFile => device.Capabilities.HasFlag(RemoteCapability.SendFile),
            RemoteWebAction.RequestFile => device.Capabilities.HasFlag(RemoteCapability.RequestFile),
            _ => false
        };
    }

    private static int GetViewMode(RemoteWebAction action)
    {
        return action == RemoteWebAction.ViewScreen ? 11 : 13;
    }

    private static bool TryGetMeshCentralBaseUri(string configuredUrl, out Uri baseUri)
    {
        if (Uri.TryCreate(configuredUrl, UriKind.Absolute, out var candidate)
            && candidate.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(candidate.UserInfo)
            && string.IsNullOrEmpty(candidate.Query)
            && string.IsNullOrEmpty(candidate.Fragment))
        {
            baseUri = candidate;
            return true;
        }

        baseUri = null!;
        return false;
    }

    private static bool IsValidNodeId(string? nodeId)
    {
        return !string.IsNullOrWhiteSpace(nodeId)
            && nodeId.Length <= 512
            && !nodeId.Any(char.IsControl);
    }
}

internal sealed class ShellExternalUriLauncher : IExternalUriLauncher
{
    public void Open(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
    }
}
