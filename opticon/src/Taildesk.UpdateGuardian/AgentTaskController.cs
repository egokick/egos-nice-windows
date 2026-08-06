using System.Diagnostics;
using System.Xml.Linq;
using Taildesk.Shared;

namespace Taildesk.UpdateGuardian;

internal sealed class AgentTaskController(GuardianPathPolicy paths)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(25);

    public async Task VerifyDefinitionAsync(CancellationToken cancellationToken)
    {
        var result = await WindowsCommand.RunAsync(
            "schtasks.exe",
            ["/Query", "/TN", RemoteAdministrationProtocol.AgentTaskName, "/XML"],
            CommandTimeout,
            cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException("The protected Agent task cannot be queried: " + result.ErrorDetail);

        XDocument document;
        try { document = XDocument.Parse(result.StandardOutput, LoadOptions.None); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidDataException("The protected Agent task definition is invalid.", exception);
        }

        var actions = document.Descendants().Where(element => element.Name.LocalName == "Exec").ToArray();
        if (actions.Length != 1)
            throw new InvalidDataException("The protected Agent task must contain exactly one executable action.");
        var action = actions[0];
        var command = action.Elements().SingleOrDefault(element => element.Name.LocalName == "Command")?.Value.Trim().Trim('"')
                      ?? string.Empty;
        paths.RequireExactPath(command, paths.AgentExecutable, "scheduled Agent executable");
        var arguments = action.Elements().SingleOrDefault(element => element.Name.LocalName == "Arguments")?.Value;
        if (!string.IsNullOrWhiteSpace(arguments))
            throw new InvalidDataException("The protected Agent task unexpectedly supplies command-line arguments.");
        var workingDirectory = action.Elements().SingleOrDefault(element => element.Name.LocalName == "WorkingDirectory")?.Value;
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            paths.RequireExactPath(workingDirectory, paths.AgentDirectory, "scheduled Agent working directory");

        var userId = document.Descendants().SingleOrDefault(element => element.Name.LocalName == "UserId")?.Value.Trim();
        if (!string.Equals(userId, "SYSTEM", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(userId, "S-1-5-18", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The protected Agent task does not run as LocalSystem.");
    }

    public async Task StopAgentOnlyAsync(CancellationToken cancellationToken)
    {
        // /End may report that the task was not running. The path-scoped process
        // check below is authoritative and deliberately avoids a name-wide kill.
        _ = await WindowsCommand.RunAsync(
            "schtasks.exe",
            ["/End", "/TN", RemoteAdministrationProtocol.AgentTaskName],
            CommandTimeout,
            cancellationToken);

        var gracefulDeadline = DateTimeOffset.UtcNow.AddSeconds(25);
        while (DateTimeOffset.UtcNow < gracefulDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processes = FindExactAgentProcesses();
            if (processes.Count == 0) return;
            foreach (var process in processes) process.Dispose();
            await Task.Delay(250, cancellationToken);
        }

        foreach (var process in FindExactAgentProcesses())
        {
            using (process)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }

        var forcedDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < forcedDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processes = FindExactAgentProcesses();
            if (processes.Count == 0) return;
            foreach (var process in processes) process.Dispose();
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException("The exact installed Taildesk Agent process did not stop.");
    }

    public async Task StartAgentAsync(CancellationToken cancellationToken)
    {
        var result = await WindowsCommand.RunAsync(
            "schtasks.exe",
            ["/Run", "/TN", RemoteAdministrationProtocol.AgentTaskName],
            CommandTimeout,
            cancellationToken);
        if (!result.Succeeded && !IsAgentRunning())
            throw new InvalidOperationException("Windows could not start the protected Agent task: " + result.ErrorDetail);
    }

    public bool IsAgentRunning()
    {
        var processes = FindExactAgentProcesses();
        try { return processes.Count > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private List<Process> FindExactAgentProcesses()
    {
        var matches = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (GuardianPathPolicy.PathEquals(process.MainModule?.FileName ?? string.Empty, paths.AgentExecutable))
                    matches.Add(process);
                else
                    process.Dispose();
            }
            catch
            {
                process.Dispose();
            }
        }
        return matches;
    }
}
