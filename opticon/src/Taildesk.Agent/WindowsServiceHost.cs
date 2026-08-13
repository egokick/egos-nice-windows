using System.Runtime.InteropServices;

namespace Taildesk.Agent;

internal static class WindowsServiceHost
{
    private const int ServiceWin32OwnProcess = 0x10;
    private const int ServiceStartPending = 2;
    private const int ServiceStopPending = 3;
    private const int ServiceRunning = 4;
    private const int ServiceStopped = 1;
    private const int ServiceAcceptStop = 1;
    private const int ServiceControlStop = 1;

    private static readonly ServiceMainCallback ServiceMainDelegate = ServiceMain;
    private static readonly HandlerCallback HandlerDelegate = Handler;
    private static Func<CancellationToken, Task>? _run;
    private static CancellationTokenSource? _stopping;
    private static IntPtr _statusHandle;
    private static int _exitCode;

    internal static int Run(Func<CancellationToken, Task> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _run = run;
        var table = new[]
        {
            new ServiceTableEntry { Name = "OpticonAgent", Callback = ServiceMainDelegate },
            new ServiceTableEntry()
        };
        if (!StartServiceCtrlDispatcher(table))
            return Marshal.GetLastWin32Error();
        return _exitCode;
    }

    private static void ServiceMain(int argumentCount, IntPtr arguments)
    {
        _statusHandle = RegisterServiceCtrlHandlerEx("OpticonAgent", HandlerDelegate, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            _exitCode = Marshal.GetLastWin32Error();
            return;
        }

        _stopping = new CancellationTokenSource();
        Report(ServiceStartPending, acceptedControls: 0, waitHint: 15_000);
        Report(ServiceRunning, ServiceAcceptStop);
        try
        {
            (_run ?? throw new InvalidOperationException("The Opticon Agent service callback is unavailable."))
                (_stopping.Token).GetAwaiter().GetResult();
            _exitCode = 0;
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            _exitCode = 0;
        }
        catch
        {
            _exitCode = 1;
        }
        finally
        {
            Report(ServiceStopped, acceptedControls: 0, win32ExitCode: _exitCode);
            _stopping.Dispose();
            _stopping = null;
        }
    }

    private static int Handler(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        if (control != ServiceControlStop || _stopping is null) return 0;
        Report(ServiceStopPending, acceptedControls: 0, waitHint: 15_000);
        _stopping.Cancel();
        return 0;
    }

    private static void Report(int state, int acceptedControls, int waitHint = 0, int win32ExitCode = 0)
    {
        if (_statusHandle == IntPtr.Zero) return;
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = acceptedControls,
            Win32ExitCode = win32ExitCode,
            WaitHint = waitHint
        };
        _ = SetServiceStatus(_statusHandle, ref status);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        public ServiceMainCallback? Callback;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainCallback(int argumentCount, IntPtr arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int HandlerCallback(int control, int eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(
        string serviceName,
        HandlerCallback callback,
        IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr statusHandle, ref ServiceStatus serviceStatus);
}
