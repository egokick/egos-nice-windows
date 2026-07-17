using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace StayActive.EnrollmentBroker.Security;

/// <summary>
/// Stores the controller API key as a CRED_TYPE_GENERIC Windows Credential
/// Manager item. Generic credentials are available only to the Windows
/// identity that owns them, so provisioning must run as the same identity as
/// the Windows service.
/// </summary>
public sealed class WindowsCredentialManagerStore : IControllerCredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string ReadGenericCredential(string targetName)
    {
        EnsureWindows();
        ValidateTargetName(targetName);

        if (!CredReadW(targetName, CredTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                throw new ControllerCredentialStoreException("The controller credential was not found in Windows Credential Manager.");
            }

            throw new ControllerCredentialStoreException("Windows Credential Manager could not read the controller credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.Type != CredTypeGeneric
                || credential.CredentialBlob == IntPtr.Zero
                || credential.CredentialBlobSize == 0
                || credential.CredentialBlobSize > 2048)
            {
                throw new ControllerCredentialStoreException("Windows Credential Manager returned an invalid controller credential.");
            }

            var bytes = new byte[checked((int)credential.CredentialBlobSize)];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return ControllerCredential.Validate(Encoding.Unicode.GetString(bytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void WriteGenericCredential(string targetName, string secret)
    {
        EnsureWindows();
        ValidateTargetName(targetName);
        var normalizedSecret = ControllerCredential.Validate(secret);
        var bytes = Encoding.Unicode.GetBytes(normalizedSecret);
        var pinnedBytes = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = Marshal.StringToCoTaskMemUni(targetName),
                CredentialBlobSize = checked((uint)bytes.Length),
                CredentialBlob = pinnedBytes.AddrOfPinnedObject(),
                Persist = CredPersistLocalMachine,
                UserName = Marshal.StringToCoTaskMemUni("StayActive EnrollmentBroker")
            };

            try
            {
                if (!CredWriteW(ref credential, 0))
                {
                    throw new ControllerCredentialStoreException("Windows Credential Manager could not store the controller credential.");
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(credential.TargetName);
                Marshal.FreeCoTaskMem(credential.UserName);
            }
        }
        finally
        {
            pinnedBytes.Free();
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void DeleteGenericCredential(string targetName)
    {
        EnsureWindows();
        ValidateTargetName(targetName);

        if (!CredDeleteW(targetName, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new ControllerCredentialStoreException("Windows Credential Manager could not delete the controller credential.");
            }
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is required for the controller credential.");
        }
    }

    private static void ValidateTargetName(string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName)
            || targetName.Length > 256
            || targetName.Any(char.IsControl))
        {
            throw new ArgumentException("A valid Windows Credential Manager target name is required.", nameof(targetName));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string targetName, uint type, uint flags, out IntPtr credentialPointer);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string targetName, uint type, uint flags);

    [DllImport("Advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credentialPointer);
}

public sealed class ControllerCredentialStoreException : Exception
{
    public ControllerCredentialStoreException(string message)
        : base(message)
    {
    }
}
