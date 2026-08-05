using System.Diagnostics;
using System.Net.Sockets;
using System.Windows.Automation;

namespace Taildesk.Admin;

internal static class RustDeskSessionLauncher
{
    private static readonly string[] SubmitButtonNames = ["connect", "ok", "submit"];

    public static async Task LaunchAsync(
        string executable,
        string tailscaleIp,
        string password,
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

        var existingProcessIds = Process.GetProcessesByName("rustdesk")
            .Select(process => process.Id)
            .ToHashSet();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--connect");
        start.ArgumentList.Add(tailscaleIp);
        var launched = Process.Start(start)
            ?? throw new InvalidOperationException("The private remote-session engine did not start.");

        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TrySubmitPassword(existingProcessIds, launched.Id, password)) return;
            await Task.Delay(200, cancellationToken);
        }

        throw new InvalidOperationException(
            "The private viewer opened, but Opticon could not securely locate its password prompt. " +
            "Use 'Copy recovery password' for this session; the password was not placed in the process command line or clipboard.");
    }

    private static bool TrySubmitPassword(HashSet<int> existingProcessIds, int launchedProcessId, string password)
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement window in windows)
        {
            try
            {
                if (TrySubmitPasswordInWindow(window, existingProcessIds, launchedProcessId, password)) return true;
            }
            catch (ElementNotAvailableException)
            {
                // RustDesk replaces its connection dialog while establishing a session.
                // Retry against the new automation tree on the next polling interval.
            }
        }
        return false;
    }

    private static bool TrySubmitPasswordInWindow(
        AutomationElement window,
        HashSet<int> existingProcessIds,
        int launchedProcessId,
        string password)
    {
        var processId = (int)window.GetCurrentPropertyValue(AutomationElement.ProcessIdProperty);
        if (processId != launchedProcessId && existingProcessIds.Contains(processId)) return false;
        if (!IsRustDeskProcess(processId)) return false;

        var passwordFields = window.FindAll(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.IsPasswordProperty, true),
                new PropertyCondition(AutomationElement.IsEnabledProperty, true)));
        if (passwordFields.Count == 0) return false;

        var passwordField = passwordFields[0];
        if (!passwordField.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject)) return false;

        var buttons = window.FindAll(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.IsEnabledProperty, true)));
        foreach (AutomationElement button in buttons)
        {
            var name = button.Current.Name.Trim();
            if (!SubmitButtonNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePatternObject)) continue;

            ((ValuePattern)valuePatternObject).SetValue(password);
            ((InvokePattern)invokePatternObject).Invoke();
            return true;
        }
        return false;
    }

    private static bool IsRustDeskProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName.Equals("rustdesk", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
