using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using System.Text;

namespace Taildesk.Shared;

public static class InvitationSigning
{
    public const string CertificateThumbprint = "FF1114DD5E2D113B4BC9EB1E65EAAE3051226A53";
    private const string CertificateBase64 = "MIIEGjCCAoKgAwIBAgIQRNSF1+rXGIVHHnrHSsrTmzANBgkqhkiG9w0BAQsFADAlMSMwIQYDVQQDDBpPcHRpY29uIEludml0YXRpb24gU2lnbmluZzAeFw0yNjA4MDQxODI2NTZaFw0zNjA4MDQxODM2NTZaMCUxIzAhBgNVBAMMGk9wdGljb24gSW52aXRhdGlvbiBTaWduaW5nMIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAzxMv0dSF+QG+nGUx/aEhIpiRzBwfCLld5KV7NFbLGnxZlQJj7WVBwWn/m6bAhRg0l1R828XBem+kr6cNB03k1r5fANCqEpqn8l39/+zWhv9DA42f3QYZVquiTt4hdjA+6onAGQ0GTOp+p50jjF49srDMv2g1gbI6W6Yv+kSfkBNSBsEuKE2qkVA6VcJlh8Wiamoc7TcS8B9JMg4OvcIWcI4Ivr+mZsuuiMycrj4l3MzcjfbuzBhOOsddjEPeZMDY329QKFFz9r/E6me7Ao6JydazEB1qFdIWU6X0r/qA/goCId0i4i/RKV/Xo9z6XrdzRDDlULp7Q10N4Ysf9nQ2fUPk7vWWW9hn3riEzm9BdVQmsRN7/P7iceLMrasdqicd73EKgZTTRB8KaDuX7B8Rxzvi4fWe5KheVqY0z2XrIR1YeO5njwFTQ3vOxTQoUCeK+EfUd9ZS5ahJAi7q1Ti972SLqAG9aOI8u5CNRR29ZDfunhkqvDXc1oZbJk+yfbj1AgMBAAGjRjBEMA4GA1UdDwEB/wQEAwIHgDATBgNVHSUEDDAKBggrBgEFBQcDAzAdBgNVHQ4EFgQUwbJIPFbMECcsczdP7pfz3fzI88UwDQYJKoZIhvcNAQELBQADggGBADIkVeO1c7lOgNVUZtUAysqw4Br+QqBCY2KaFH1IDmj7DEwDazdYyG4pXee+ypRaF7HXbDXPj4QDl62xBmtnr17rWbjZfpXSSMKf0HG/p205DmfGs/0xCXP4UkY8juegArO69+XpVUjFtIADq1f0RqgaP/JT+UPGHbCJt/URYA0kdEHhIHVzmiFmxHUquarVRShtyIrRtGStdXvgOl3+TesjlqykB9AdCDU0ZyTCFdnm+ATU30Dvo4yOa+A8XeT07aqL1e2UIq77Inm3Uk5H9P3FplbCIsO7hK3bf7iSbym/XxTA7Qn0ut8zd4QwLvbymQGoUgGQKgjFZmEEZ/L9R+pS8LvZQUfoz5OOO4JGdfzjdY8WjI5EG3jwJLYAV6U/ZJ8za49ZSt5Khp7bu2luyD6g6EKgv8fcB3kwbzUtZl3/j+bn5XazkGyxXQ33IQ1d8cADXAkGgURbFVpzTLl29lbmbea87YyFy5PO6eM+yWMsIBL/U85lrDP1qU7dCd5Y0g==";

    public static X509Certificate2 PinnedCertificate { get; } = new(Convert.FromBase64String(CertificateBase64));

    public static byte[] Sign(ReadOnlySpan<byte> data)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var certificate = store.Certificates.Find(X509FindType.FindByThumbprint, CertificateThumbprint, false)
            .OfType<X509Certificate2>().FirstOrDefault(item => item.HasPrivateKey)
            ?? throw new InvalidOperationException("The Opticon invitation-signing key is unavailable on this command center.");
        using (certificate)
        using (var rsa = GetSigningKey(certificate))
        {
            return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
    }

    private static RSA GetSigningKey(X509Certificate2 certificate)
    {
        try
        {
            return certificate.GetRSAPrivateKey()
                ?? throw new InvalidOperationException("The Opticon invitation-signing certificate has no RSA private key.");
        }
        catch (Exception exception) when (exception.Message.Contains("ephemeral", StringComparison.OrdinalIgnoreCase)
            && (exception is CryptographicException or ArgumentException))
        {
            return OpenEphemeralSigningKey(certificate);
        }
    }

    // A certificate-store import can label a persisted CNG key as ephemeral. In
    // that case X509Certificate2 opens it without the option Windows requires.
    private static RSA OpenEphemeralSigningKey(X509Certificate2 certificate)
    {
        const uint keyProviderInfoProperty = 2;
        const uint machineKeySet = 0x20;

        uint byteCount = 0;
        if (!CertGetCertificateContextProperty(certificate.Handle, keyProviderInfoProperty, IntPtr.Zero, ref byteCount) || byteCount == 0)
            throw new CryptographicException("The Opticon invitation-signing key provider information is unavailable.");

        var buffer = Marshal.AllocHGlobal(checked((int)byteCount));
        try
        {
            if (!CertGetCertificateContextProperty(certificate.Handle, keyProviderInfoProperty, buffer, ref byteCount))
                throw new CryptographicException("Windows could not read the Opticon invitation-signing key provider information.");

            var keyInfo = Marshal.PtrToStructure<CryptKeyProviderInfo>(buffer);
            var keyName = Marshal.PtrToStringUni(keyInfo.ContainerName);
            var providerName = Marshal.PtrToStringUni(keyInfo.ProviderName);
            if (string.IsNullOrWhiteSpace(keyName) || string.IsNullOrWhiteSpace(providerName) || keyInfo.ProviderType != 0)
                throw new CryptographicException("The Opticon invitation-signing key is not a supported CNG key.");

            var openOptions = (keyInfo.Flags & machineKeySet) != 0
                ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;
            return new RSACng(CngKey.Open(keyName, new CngProvider(providerName), openOptions));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptKeyProviderInfo
    {
        public IntPtr ContainerName;
        public IntPtr ProviderName;
        public uint ProviderType;
        public uint Flags;
        public uint ParameterCount;
        public IntPtr Parameters;
        public uint KeySpec;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertGetCertificateContextProperty(
        IntPtr certificateContext,
        uint propertyId,
        IntPtr data,
        ref uint dataSize);

    public static bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        using var rsa = PinnedCertificate.GetRSAPublicKey() ?? throw new InvalidDataException("The pinned Opticon invitation certificate is invalid.");
        return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    public static async Task SignAuthenticodeAsync(string path, CancellationToken cancellationToken = default)
    {
        var pathBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(path)));
        var command = $"$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{pathBase64}'));$c=Get-Item 'Cert:\\CurrentUser\\My\\{CertificateThumbprint}';$s=Set-AuthenticodeSignature -LiteralPath $p -Certificate $c -HashAlgorithm SHA256;if(-not $s.SignerCertificate -or $s.SignerCertificate.Thumbprint -ne '{CertificateThumbprint}'){{Write-Error 'Authenticode signing failed';exit 9}}";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var result = await ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", encodedCommand], TimeSpan.FromMinutes(2), cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException("Windows could not Authenticode-sign the invitation: " + result.StandardError.Trim());
    }

    public static Task VerifyAuthenticodeAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AuthenticodeFileVerifier.VerifyPinned(path, PinnedCertificate);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
