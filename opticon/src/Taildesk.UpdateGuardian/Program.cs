using System.Security.Principal;
using Taildesk.Shared;

namespace Taildesk.UpdateGuardian;

internal static class Program
{
    private const string MutexName = @"Global\Taildesk.UpdateGuardian.v1";

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Taildesk Update Guardian can run only on Windows.");
            return 2;
        }

        if (SshSupervisor.IsRequested(args))
            return await SshSupervisor.RunFromArgumentsAsync(args, CancellationToken.None);
        if (SshAdminProbe.IsRequested(args))
            return SshAdminProbe.Run(args);
        var watchdogOnly = args.Length == 1
                           && args[0].Equals(
                               RemoteAdministrationProtocol.GuardianWatchdogArgument, StringComparison.Ordinal);
        if (args.Length != 0 && !watchdogOnly)
        {
            Console.Error.WriteLine("Taildesk Update Guardian accepts only its fixed supervisor/watchdog modes; paths and arbitrary actions are rejected.");
            return 2;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null || !identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid))
        {
            Console.Error.WriteLine("Taildesk Update Guardian must run as LocalSystem.");
            return 2;
        }

        using var mutex = new Mutex(initiallyOwned: false, MutexName);
        var ownsMutex = false;
        try
        {
            // A watchdog never queues behind a full recovery invocation. A full
            // ONSTART/manual invocation does wait through a quick watchdog so a
            // simultaneous boot trigger cannot suppress committed boot health.
            var mutexWait = watchdogOnly ? TimeSpan.Zero : TimeSpan.FromMinutes(2);
            try { ownsMutex = mutex.WaitOne(mutexWait); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
            {
                // A full Guardian already watches the active transaction, or a
                // watchdog found one busy. The next minute/manual retry is safe.
                return 0;
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            // A Windows mutex is owned by the acquiring thread. Do not await
            // here: an async continuation may resume on another pool thread,
            // which would make ReleaseMutex fail even though this process owns
            // the named mutex. The runner remains asynchronous internally while
            // this entry thread waits and retains mutex ownership.
            return new GuardianRunner()
                .RunAsync(watchdogOnly, cancellation.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Taildesk Update Guardian was cancelled.");
            return 3;
        }
        catch (Exception exception)
        {
            UpdateGuardianStartupDiagnostics.TryWrite(
                watchdogOnly ? "watchdog" : "full",
                exception);
            Console.Error.WriteLine("Taildesk Update Guardian failed: " + exception);
            return 1;
        }
        finally
        {
            if (ownsMutex) mutex.ReleaseMutex();
        }
    }
}
