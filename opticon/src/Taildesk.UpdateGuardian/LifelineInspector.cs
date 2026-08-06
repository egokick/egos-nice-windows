using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Taildesk.Shared;

namespace Taildesk.UpdateGuardian;

internal sealed class LifelineInspector
{
    private const string TailscaleServiceName = "Tailscale";
    private const int RustDeskPort = 21118;

    public LifelineSnapshot CapturePreflight(string bindAddress)
    {
        Validate(bindAddress, requireSsh: false);
        return new LifelineSnapshot(IsListenerActive(RemoteAdministrationProtocol.SshPort, bindAddress));
    }

    public void Validate(string bindAddress, bool requireSsh)
    {
        if (!IsServiceRunning(TailscaleServiceName))
            throw new InvalidOperationException("The Tailscale service is not running; activation is refused.");
        if (!IsLocalAddressAssigned(bindAddress))
            throw new InvalidOperationException("The journaled Tailscale address is not assigned to an active local interface.");
        if (!IsRustDeskRunning() || !IsListenerActive(RustDeskPort, bindAddress: null))
            throw new InvalidOperationException("The RustDesk process or TCP 21118 recovery listener is not healthy.");
        if (requireSsh && !IsListenerActive(RemoteAdministrationProtocol.SshPort, bindAddress))
            throw new InvalidOperationException("The SSH lifeline that was active before activation stopped listening.");
    }

    private static bool IsLocalAddressAssigned(string value)
    {
        if (!IPAddress.TryParse(value, out var expected)) return false;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Any(address => address.Address.Equals(expected));
        }
        catch { return false; }
    }

    private static bool IsRustDeskRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("rustdesk");
            try { return processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch { return false; }
    }

    private static bool IsListenerActive(int port, string? bindAddress)
    {
        try
        {
            var expected = string.IsNullOrWhiteSpace(bindAddress) ? null : IPAddress.Parse(bindAddress);
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint =>
                endpoint.Port == port
                && (expected is null
                    || endpoint.Address.Equals(expected)
                    || endpoint.Address.Equals(IPAddress.Any)
                    || endpoint.Address.Equals(IPAddress.IPv6Any)));
        }
        catch { return false; }
    }

    private static bool IsServiceRunning(string serviceName)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not inspect service state.");
        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero) return false;
            try
            {
                var status = new ServiceStatusProcess();
                var size = Marshal.SizeOf<ServiceStatusProcess>();
                if (!QueryServiceStatusEx(service, ScStatusProcessInfo, ref status, size, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Windows could not inspect {serviceName}.");
                return status.CurrentState == ServiceRunning;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr serviceManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service,
        int infoLevel,
        ref ServiceStatusProcess buffer,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}

internal sealed record LifelineSnapshot(bool SshWasListening);
