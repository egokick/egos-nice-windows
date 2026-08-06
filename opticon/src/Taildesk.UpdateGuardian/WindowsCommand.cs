using System.Diagnostics;

namespace Taildesk.UpdateGuardian;

internal static class WindowsCommand
{
    public static async Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"Windows could not start {Path.GetFileName(executable)}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{Path.GetFileName(executable)} did not finish within {timeout}.");
        }

        return new CommandResult(process.ExitCode, await standardOutput, await standardError);
    }
}

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
    public string ErrorDetail => new[] { StandardError, StandardOutput }
        .Select(value => value.Trim())
        .FirstOrDefault(value => value.Length > 0) ?? $"exit code {ExitCode}";
}
