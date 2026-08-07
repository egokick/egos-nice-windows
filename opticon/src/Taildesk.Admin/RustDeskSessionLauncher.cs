using System.Diagnostics;
using System.Net.Sockets;
using Taildesk.Shared;

namespace Taildesk.Admin;

internal static class RustDeskSessionLauncher
{
    public static async Task LaunchAsync(
        string executable,
        string tailscaleIp,
        string password,
        bool privacyMode2Enabled,
        CancellationToken cancellationToken)
    {
        using (var probe = new TcpClient())
        {
            try
            {
                await probe.ConnectAsync(tailscaleIp, 21118, cancellationToken)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AccessDenied)
            {
                throw new InvalidOperationException(
                    "Windows or another VPN blocked Opticon from opening the private Tailscale connection. " +
                    "Run System checks and repair the NordVPN private-mesh application exclusions.",
                    exception);
            }
            catch (Exception exception) when (exception is SocketException or TimeoutException)
            {
                throw new InvalidOperationException(
                    $"The device is not accepting private remote-control connections at {tailscaleIp}:21118.",
                    exception);
            }
        }
        ConfigurePeerPrivacyMode2(tailscaleIp, privacyMode2Enabled);

        var executableDirectory = Path.GetDirectoryName(executable);
        if (string.IsNullOrWhiteSpace(executableDirectory))
            throw new InvalidOperationException("The private remote-session engine path has no parent directory.");

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            // Never let a long-lived RustDesk viewer inherit Opticon's install
            // directory. Windows refuses to swap that directory during an
            // update while any child process uses it as its current directory.
            WorkingDirectory = executableDirectory
        };
        start.ArgumentList.Add("--connect");
        start.ArgumentList.Add(tailscaleIp);
        // RustDesk applies this password to the connection opened by --connect.  Passing
        // it through its supported command-line contract avoids depending on the viewer's
        // version-specific password dialog and does not copy the secret to the clipboard.
        start.ArgumentList.Add("--password");
        start.ArgumentList.Add(password);
        _ = Process.Start(start)
            ?? throw new InvalidOperationException("The private remote-session engine did not start.");
    }

    private static void ConfigurePeerPrivacyMode2(string tailscaleIp, bool enabled)
    {
        var peerDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RustDesk", "config", "peers");
        Directory.CreateDirectory(peerDirectory);
        var peerFile = Path.Combine(peerDirectory, tailscaleIp + ".toml");
        var existing = File.Exists(peerFile) ? File.ReadAllText(peerFile) : string.Empty;
        var configured = RustDeskConfiguration.ConfigurePeerPrivacyMode2(existing, enabled);
        var temporaryFile = peerFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryFile, configured, new System.Text.UTF8Encoding(false));
            File.Move(temporaryFile, peerFile, true);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Opticon could not {(enabled ? "enable" : "disable")} RustDesk Privacy Mode 2 for {tailscaleIp}.",
                exception);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
            }
            catch { }
        }
    }

}
