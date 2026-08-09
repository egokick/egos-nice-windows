using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Taildesk.UpdateGuardian;

internal static class WindowsCommand
{
    public static async Task<CommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(executable))
            throw new InvalidDataException("Privileged Guardian commands require an absolute executable path.");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)
            || (File.GetAttributes(executable) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new FileNotFoundException("The privileged Guardian command is missing or unsafe.", executable);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ApplySanitizedEnvironment(startInfo);
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

    public static string RequireSystemExecutable(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
            throw new InvalidDataException("A fixed Windows system executable name is invalid.");
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(system))
            throw new DirectoryNotFoundException("The Windows System32 directory is unavailable.");
        var executable = Path.GetFullPath(Path.Combine(system, fileName));
        if (!File.Exists(executable)
            || (File.GetAttributes(executable) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new FileNotFoundException("A required Windows system executable is missing or unsafe.", executable);
        return executable;
    }

    private static void ApplySanitizedEnvironment(ProcessStartInfo startInfo)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(windows) || string.IsNullOrWhiteSpace(system))
            throw new DirectoryNotFoundException("The fixed Windows directories are unavailable.");

        startInfo.Environment.Clear();
        Set(startInfo, "SystemRoot", windows);
        Set(startInfo, "WINDIR", windows);
        Set(startInfo, "SystemDrive", Path.GetPathRoot(windows));
        Set(startInfo, "ProgramData", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Set(startInfo, "ProgramFiles", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Set(startInfo, "ProgramW6432", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Set(startInfo, "ProgramFiles(x86)", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Set(startInfo, "ComSpec", Path.Combine(system, "cmd.exe"));
        Set(startInfo, "PATH", string.Join(Path.PathSeparator, system, windows));
        Set(startInfo, "TEMP", Path.Combine(windows, "Temp"));
        Set(startInfo, "TMP", Path.Combine(windows, "Temp"));
        Set(startInfo, "PROCESSOR_ARCHITECTURE", RuntimeInformation.OSArchitecture.ToString().ToUpperInvariant());
    }

    private static void Set(ProcessStartInfo startInfo, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) startInfo.Environment[name] = value;
    }
}

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
    public string ErrorDetail => new[] { StandardError, StandardOutput }
        .Select(value => value.Trim())
        .FirstOrDefault(value => value.Length > 0) ?? $"exit code {ExitCode}";
}
