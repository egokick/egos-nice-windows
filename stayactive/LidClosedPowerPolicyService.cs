using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace StayActive;

internal interface IPowerPolicyProcessRunner
{
    string RunAndCapture(string arguments, TimeSpan timeout);

    void RunAndWait(string arguments, TimeSpan timeout);
}

internal sealed class SystemPowerPolicyProcessRunner : IPowerPolicyProcessRunner
{
    public string RunAndCapture(string arguments, TimeSpan timeout)
    {
        using var process = CreateProcess(arguments, redirectOutput: true);
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            TryKill(process);
            throw new TimeoutException("powercfg did not finish in time.");
        }

        var output = standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"powercfg exited with code {process.ExitCode}: {output.Trim()}");
        }

        return output;
    }

    public void RunAndWait(string arguments, TimeSpan timeout)
    {
        using var process = CreateProcess(arguments, redirectOutput: false);
        process.Start();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            TryKill(process);
            throw new TimeoutException("powercfg did not finish in time.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"powercfg exited with code {process.ExitCode}.");
        }
    }

    private static Process CreateProcess(string arguments, bool redirectOutput)
    {
        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = redirectOutput,
                RedirectStandardError = redirectOutput
            }
        };
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        catch
        {
        }
    }
}

/// <summary>
/// Applies the system-wide power-plan policy used by the "keep on when lid is closed" menu item.
/// The disabled policy deliberately uses normal, finite timeouts: closing the lid sleeps immediately,
/// then the machine hibernates after a while instead of remaining powered indefinitely.
/// </summary>
internal sealed class LidClosedPowerPolicyService
{
    private const string Scheme = "SCHEME_CURRENT";
    private const string SleepGroup = "SUB_SLEEP";
    private const string SleepAfter = "STANDBYIDLE";
    private const string HibernateAfter = "HIBERNATEIDLE";
    private const string ButtonsGroup = "4f971e89-eebd-4455-a8de-9e59040e7347";
    private const string LidAction = "5ca83367-6e45-459f-a27b-476b1d01c936";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex CurrentValuePattern = new(
        @"Current (?<source>AC|DC) Power Setting Index:\s*0x(?<value>[0-9a-fA-F]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IPowerPolicyProcessRunner _runner;

    public LidClosedPowerPolicyService()
        : this(new SystemPowerPolicyProcessRunner())
    {
    }

    internal LidClosedPowerPolicyService(IPowerPolicyProcessRunner runner)
    {
        _runner = runner;
    }

    public void SetKeepAwakeWhenLidClosed(bool enabled)
    {
        // Index 0 is "Do nothing" for the lid action. Index 1 is "Sleep".
        SetBothPowerSources(ButtonsGroup, LidAction, enabled ? 0 : 1, enabled ? 0 : 1);

        // Zero means Never. When disabling, use finite defaults so the laptop returns to a
        // low-power state after it has been left alone; lid close itself sleeps immediately.
        SetBothPowerSources(SleepGroup, SleepAfter, enabled ? 0 : 900, enabled ? 0 : 1800);
        SetBothPowerSources(SleepGroup, HibernateAfter, enabled ? 0 : 3600, enabled ? 0 : 10800);
        _runner.RunAndWait($"/setactive {Scheme}", CommandTimeout);
    }

    public bool IsKeepAwakeWhenLidClosedConfigured()
    {
        return HasZeroTimeout(SleepAfter) && HasZeroTimeout(HibernateAfter);
    }

    private void SetBothPowerSources(string subgroup, string setting, int dcValue, int acValue)
    {
        _runner.RunAndWait($"/setdcvalueindex {Scheme} {subgroup} {setting} {dcValue}", CommandTimeout);
        _runner.RunAndWait($"/setacvalueindex {Scheme} {subgroup} {setting} {acValue}", CommandTimeout);
    }

    private bool HasZeroTimeout(string setting)
    {
        var output = _runner.RunAndCapture($"/q {Scheme} {SleepGroup} {setting}", CommandTimeout);
        var values = CurrentValuePattern.Matches(output)
            .Select(match => int.Parse(match.Groups["value"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .ToArray();
        return values.Length == 2 && values.All(value => value == 0);
    }
}
