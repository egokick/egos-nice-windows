using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Taildesk.Shared;

namespace Taildesk.UpdateGuardian;

/// <summary>
/// Acquires and verifies the exact full interactive token used by the isolated
/// same-user OpenSSH daemon. This deliberately avoids changing the machine-wide
/// LocalAccountTokenFilterPolicy UAC setting.
/// </summary>
internal static class SshAdminToken
{
    private const int Logon32LogonInteractive = 2;
    private const int Logon32ProviderDefault = 0;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenElevationTypeClass = 18;
    private const int TokenLinkedTokenClass = 19;
    private const int TokenElevationClass = 20;
    private const int TokenIntegrityLevelClass = 25;
    private const int TokenTypeClass = 8;
    private const int TokenElevationTypeDefault = 1;
    private const int TokenElevationTypeFull = 2;
    private const int TokenElevationTypeLimited = 3;
    private const int ErrorInsufficientBuffer = 122;
    private const int SecurityMandatoryHighRid = 0x3000;
    private const int SecurityMandatorySystemRid = 0x4000;
    private const uint ScManagerCreateService = 0x0002;

    public static SafeAccessTokenHandle LogonFullAdministrator(IntPtr password)
    {
        if (password == IntPtr.Zero) throw new ArgumentException("A protected account password is required.", nameof(password));
        if (!LogonUserW(
                RemoteAdministrationProtocol.SshAccountName,
                Environment.MachineName,
                password,
                Logon32LogonInteractive,
                Logon32ProviderDefault,
                out var original))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows policy did not permit an interactive token for the dedicated Opticon SSH administrator.");
        }

        using (original)
        {
            var originalElevationType = ReadInt32(original, TokenElevationTypeClass, "elevation type");
            SafeAccessTokenHandle? linked = null;
            try
            {
                var source = original;
                if (originalElevationType == TokenElevationTypeLimited)
                {
                    linked = ReadLinkedToken(original);
                    source = linked;
                }

                if (!DuplicateTokenEx(
                        source,
                        desiredAccess: 0,
                        IntPtr.Zero,
                        SecurityImpersonation,
                        TokenPrimary,
                        out var primary))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not duplicate the SSH administrator token.");

                try
                {
                    _ = Inspect(primary, challenge: string.Empty, requireAdministrativeCapability: true);
                    return primary;
                }
                catch
                {
                    primary.Dispose();
                    throw;
                }
            }
            finally
            {
                linked?.Dispose();
            }
        }
    }

    public static SshAdminAttestation InspectCurrent(string challenge)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return Inspect(identity.AccessToken, challenge, requireAdministrativeCapability: true);
    }

    public static SshAdminAttestation Inspect(
        SafeAccessTokenHandle token,
        string challenge,
        bool requireAdministrativeCapability)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.IsInvalid || token.IsClosed) throw new InvalidOperationException("The SSH administrator token is unavailable.");

        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        var expectedSid = (SecurityIdentifier)new NTAccount(
                Environment.MachineName,
                RemoteAdministrationProtocol.SshAccountName)
            .Translate(typeof(SecurityIdentifier));
        if (identity.User is null || !identity.User.Equals(expectedSid)
            || identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid))
            throw new UnauthorizedAccessException("The SSH process is not running as the dedicated Opticon administrator.");

        var tokenType = ReadInt32(token, TokenTypeClass, "token type");
        if (tokenType != TokenPrimary)
            throw new UnauthorizedAccessException("The SSH administrator does not have a primary process token.");

        var elevation = ReadInt32(token, TokenElevationClass, "elevation state") != 0;
        var elevationTypeValue = ReadInt32(token, TokenElevationTypeClass, "elevation type");
        var elevationType = elevationTypeValue switch
        {
            TokenElevationTypeDefault => "default",
            TokenElevationTypeFull => "full",
            TokenElevationTypeLimited => "limited",
            _ => "unknown"
        };
        if (!elevation || elevationTypeValue == TokenElevationTypeLimited)
            throw new UnauthorizedAccessException("The SSH administrator token is filtered instead of elevated.");

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var administratorsEnabled = new WindowsPrincipal(identity).IsInRole(administrators);
        if (!administratorsEnabled)
            throw new UnauthorizedAccessException("The SSH administrator token does not have an enabled Administrators SID.");

        var integrityRid = ReadIntegrityRid(token);
        if (integrityRid < SecurityMandatoryHighRid || integrityRid >= SecurityMandatorySystemRid)
            throw new UnauthorizedAccessException("The SSH administrator token is not a non-SYSTEM high-integrity token.");

        var administrativeCapability = !requireAdministrativeCapability || ProveAdministrativeCapability(token);
        if (!administrativeCapability)
            throw new UnauthorizedAccessException("The SSH administrator token cannot open the Service Control Manager with administrative rights.");

        return new SshAdminAttestation
        {
            Challenge = challenge,
            UserSid = identity.User.Value,
            UserName = identity.Name,
            Elevated = elevation,
            ElevationType = elevationType,
            IntegrityRid = integrityRid,
            AdministratorsEnabled = administratorsEnabled,
            AdministrativeCapability = administrativeCapability,
            TokenType = "primary"
        };
    }

    private static bool ProveAdministrativeCapability(SafeAccessTokenHandle token)
    {
        var opened = false;
        WindowsIdentity.RunImpersonated(token, () =>
        {
            var manager = OpenSCManagerW(null, null, ScManagerCreateService);
            if (manager == IntPtr.Zero) return;
            try { opened = true; }
            finally { _ = CloseServiceHandle(manager); }
        });
        return opened;
    }

    private static SafeAccessTokenHandle ReadLinkedToken(SafeAccessTokenHandle token)
    {
        // TOKEN_LINKED_TOKEN is a fixed one-handle structure. Some supported
        // Windows builds return ERROR_BAD_LENGTH (while reporting the correct
        // size) for the usual zero-length sizing probe, so query it directly.
        var expectedLength = checked((uint)Marshal.SizeOf<TokenLinkedToken>());
        if (!GetLinkedTokenInformation(
                token,
                TokenLinkedTokenClass,
                out var information,
                expectedLength,
                out var returnedLength))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Windows could not read the SSH linked administrator token.");

        var linked = new SafeAccessTokenHandle(information.LinkedToken);
        if (returnedLength != expectedLength || linked.IsInvalid)
        {
            linked.Dispose();
            throw new InvalidDataException("Windows returned an invalid linked administrator token structure.");
        }
        return linked;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenLinkedToken
    {
        public IntPtr LinkedToken;
    }

    private static int ReadIntegrityRid(SafeAccessTokenHandle token)
    {
        var buffer = ReadTokenInformation(token, TokenIntegrityLevelClass, "integrity level");
        try
        {
            var sidPointer = Marshal.ReadIntPtr(buffer);
            if (sidPointer == IntPtr.Zero) throw new InvalidOperationException("Windows returned an empty integrity SID.");
            var sid = new SecurityIdentifier(sidPointer).Value;
            var separator = sid.LastIndexOf('-');
            if (separator < 0 || !int.TryParse(
                    sid.AsSpan(separator + 1),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var rid))
                throw new InvalidDataException("Windows returned an invalid integrity SID.");
            return rid;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ReadInt32(SafeAccessTokenHandle token, int informationClass, string description)
    {
        // TOKEN_ELEVATION_TYPE, TOKEN_ELEVATION, and TOKEN_TYPE are all
        // fixed DWORD-sized values. Supported Windows builds do not
        // consistently set ERROR_INSUFFICIENT_BUFFER for a zero-length probe
        // of these classes, and the stale last-error value can misleadingly
        // report ERROR_NOT_ALL_ASSIGNED. Query the bounded value directly.
        const uint expectedLength = sizeof(int);
        if (!GetInt32TokenInformation(
                token,
                informationClass,
                out var information,
                expectedLength,
                out var returnedLength))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Windows could not read the SSH {description}.");
        if (returnedLength != expectedLength)
            throw new InvalidDataException($"Windows returned an invalid SSH {description} structure.");
        return information;
    }

    private static IntPtr ReadTokenInformation(
        SafeAccessTokenHandle token,
        int informationClass,
        string description)
    {
        _ = GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var length);
        var sizeError = Marshal.GetLastWin32Error();
        if (length == 0 || sizeError != ErrorInsufficientBuffer)
            throw new Win32Exception(sizeError, $"Windows could not size the SSH {description}.");
        if (length > 64 * 1024) throw new InvalidDataException($"The SSH {description} exceeds its bounded size.");

        var buffer = Marshal.AllocHGlobal(checked((int)length));
        if (!GetTokenInformation(token, informationClass, buffer, length, out _))
        {
            var error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(buffer);
            throw new Win32Exception(error, $"Windows could not read the SSH {description}.");
        }
        return buffer;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUserW(
        string userName,
        string domain,
        IntPtr password,
        int logonType,
        int logonProvider,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetInt32TokenInformation(
        SafeAccessTokenHandle token,
        int informationClass,
        out int information,
        uint informationLength,
        out uint returnLength);

    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLinkedTokenInformation(
        SafeAccessTokenHandle token,
        int informationClass,
        out TokenLinkedToken information,
        uint informationLength,
        out uint returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
