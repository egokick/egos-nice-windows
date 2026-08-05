using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Taildesk.Shared;

public enum SecretScope
{
    CurrentUser,
    LocalMachine
}

public static class SecretProtector
{
    private const int CryptprotectUiForbidden = 0x1;
    private const int CryptprotectLocalMachine = 0x4;

    public static string Protect(string plaintext, SecretScope scope = SecretScope.CurrentUser)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        EnsureWindows();
        var input = Encoding.UTF8.GetBytes(plaintext);
        var inputBlob = Blob.FromBytes(input);
        try
        {
            var flags = CryptprotectUiForbidden | (scope == SecretScope.LocalMachine ? CryptprotectLocalMachine : 0);
            if (!CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags, out var outputBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                return Convert.ToBase64String(outputBlob.ToBytes());
            }
            finally
            {
                LocalFree(outputBlob.Data);
            }
        }
        finally
        {
            inputBlob.Free();
            CryptographicOperations.ZeroMemory(input);
        }
    }

    public static string Unprotect(string protectedValue, SecretScope scope = SecretScope.CurrentUser)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return string.Empty;
        }

        EnsureWindows();
        var encrypted = Convert.FromBase64String(protectedValue);
        var inputBlob = Blob.FromBytes(encrypted);
        try
        {
            // The protection scope is encoded in the DPAPI blob. Microsoft only
            // documents UI_FORBIDDEN and VERIFY_PROTECTION for CryptUnprotectData;
            // LOCAL_MACHINE is a CryptProtectData flag.
            _ = scope;
            var flags = CryptprotectUiForbidden;
            if (!CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags, out var outputBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var clear = outputBlob.ToBytes();
                try
                {
                    return Encoding.UTF8.GetString(clear);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(clear);
                }
            }
            finally
            {
                LocalFree(outputBlob.Data);
            }
        }
        finally
        {
            inputBlob.Free();
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI is required.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Length;
        public IntPtr Data;

        public static Blob FromBytes(byte[] bytes)
        {
            var blob = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
            return blob;
        }

        public readonly byte[] ToBytes()
        {
            var bytes = new byte[Length];
            Marshal.Copy(Data, bytes, 0, Length);
            return bytes;
        }

        public void Free()
        {
            if (Data == IntPtr.Zero)
            {
                return;
            }

            Marshal.FreeHGlobal(Data);
            Data = IntPtr.Zero;
            Length = 0;
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref Blob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out Blob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref Blob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out Blob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
