namespace StayActive.Remotes;

internal interface IRemoteAdminConsoleLauncher
{
    RemoteActionResult Open();
}

/// <summary>
/// Opens only the configured owner-operated administration console. It carries
/// no credentials in the URL; the browser performs the console's normal OIDC
/// sign-in flow.
/// </summary>
internal sealed class RemoteAdminConsoleLauncher : IRemoteAdminConsoleLauncher
{
    private readonly Func<RemoteClientPreferences> _getPreferences;
    private readonly IExternalUriLauncher _uriLauncher;

    public RemoteAdminConsoleLauncher(
        Func<RemoteClientPreferences> getPreferences,
        IExternalUriLauncher? uriLauncher = null)
    {
        _getPreferences = getPreferences ?? throw new ArgumentNullException(nameof(getPreferences));
        _uriLauncher = uriLauncher ?? new ShellExternalUriLauncher();
    }

    public RemoteActionResult Open()
    {
        if (!TryGetConsoleUri(_getPreferences().AdminConsoleUrl, out var consoleUri))
        {
            return RemoteActionResult.Failure("Configure an HTTPS URL for your self-hosted administration console.");
        }

        try
        {
            _uriLauncher.Open(consoleUri);
            return RemoteActionResult.Success("Opening the self-hosted administration console.");
        }
        catch (Exception ex)
        {
            return RemoteActionResult.Failure($"Could not open the self-hosted administration console: {ex.Message}");
        }
    }

    internal static bool TryGetConsoleUri(string configuredUrl, out Uri consoleUri)
    {
        if (RemoteClientPreferences.IsSelfHostedEndpoint(configuredUrl)
            && Uri.TryCreate(configuredUrl, UriKind.Absolute, out var candidate))
        {
            consoleUri = candidate;
            return true;
        }

        consoleUri = null!;
        return false;
    }
}
