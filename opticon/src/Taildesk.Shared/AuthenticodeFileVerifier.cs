using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Taildesk.Shared;

internal static class AuthenticodeFileVerifier
{
    private const int Success = 0;
    private const int CertificateUntrustedRoot = unchecked((int)0x800B0109);
    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateActionIgnore = 0;
    private const uint CacheOnlyUrlRetrieval = 0x00001000;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static void VerifyPinned(string path, X509Certificate2 expectedSigner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expectedSigner);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Authenticode verification requires Windows.");

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The signed executable is missing.", fullPath);

        var trustResult = VerifyWithWindows(fullPath);
        // The Opticon publisher certificate is deliberately pinned and self-signed.
        // Windows therefore returns CERT_E_UNTRUSTEDROOT on machines where that
        // exact leaf is not installed as a root. No other indeterminate trust or
        // digest result is accepted.
        if (trustResult is not Success and not CertificateUntrustedRoot)
            throw new InvalidDataException(
                $"Windows rejected the Authenticode signature (0x{unchecked((uint)trustResult):X8}).");

        try
        {
            using var embedded = new X509Certificate2(X509Certificate.CreateFromSignedFile(fullPath));
            if (embedded.RawData.Length != expectedSigner.RawData.Length
                || !CryptographicOperations.FixedTimeEquals(embedded.RawData, expectedSigner.RawData))
                throw new InvalidDataException("The Authenticode signer does not exactly match the pinned Opticon certificate.");
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("The executable has no readable Authenticode signer.", exception);
        }
    }

    private static int VerifyWithWindows(string path)
    {
        var pathPointer = Marshal.StringToCoTaskMemUni(path);
        var fileInfoPointer = IntPtr.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructureSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                FilePath = pathPointer
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

            var trustData = new WinTrustData
            {
                StructureSize = checked((uint)Marshal.SizeOf<WinTrustData>()),
                UiChoice = UiNone,
                RevocationChecks = RevokeNone,
                UnionChoice = ChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = StateActionIgnore,
                ProviderFlags = CacheOnlyUrlRetrieval
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(new IntPtr(-1), ref action, ref trustData);
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero) Marshal.FreeHGlobal(fileInfoPointer);
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructureSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructureSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);
}
