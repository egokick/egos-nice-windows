using System.Diagnostics;

namespace Taildesk.Shared;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed class ProcessTimeoutException : TimeoutException
{
    public ProcessTimeoutException(
        string executable,
        TimeSpan timeout,
        string standardOutput,
        string standardError)
        : base($"{Path.GetFileName(executable)} did not finish in time.")
    {
        Executable = executable;
        Timeout = timeout;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public string Executable { get; }
    public TimeSpan Timeout { get; }
    public string StandardOutput { get; }
    public string StandardError { get; }
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        bool captureOutput = true)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {executable}.");
        }

        using var timeoutSource = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource?.Token ?? CancellationToken.None);
        // A process can exit while a child it spawned still owns its redirected
        // handles.  Read the streams under the same deadline as the process so
        // that this case is reported as a timeout rather than hanging forever.
        var outputTask = captureOutput
            ? process.StandardOutput.ReadToEndAsync(linked.Token)
            : Task.FromResult(string.Empty);
        var errorTask = captureOutput
            ? process.StandardError.ReadToEndAsync(linked.Token)
            : Task.FromResult(string.Empty);

        try
        {
            await Task.WhenAll(process.WaitForExitAsync(linked.Token), outputTask, errorTask);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            if (timeoutSource?.IsCancellationRequested == true)
            {
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
                throw new ProcessTimeoutException(
                    executable,
                    timeout!.Value,
                    await ReadCompletedOutputAsync(outputTask),
                    await ReadCompletedOutputAsync(errorTask));
            }
            throw;
        }
        return new ProcessResult(process.ExitCode, outputTask.Result, errorTask.Result);
    }

    private static async Task<string> ReadCompletedOutputAsync(Task<string> outputTask)
    {
        try { return await outputTask; }
        catch (OperationCanceledException) { return string.Empty; }
    }

    public static string? FindOnPath(params string[] executableNames)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in executableNames)
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
