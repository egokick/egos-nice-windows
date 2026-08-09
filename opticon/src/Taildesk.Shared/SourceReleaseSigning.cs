using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Taildesk.Shared;

/// <summary>
/// Offline trust root for source archives, update manifests, and command-center
/// package manifests. It is intentionally independent of the online invite key.
/// </summary>
public static class SourceReleaseSigning
{
    public static X509Certificate2 PinnedCertificate { get; } = LoadPinnedCertificate();
    public static string CertificateThumbprint { get; } = NormalizeThumbprint(PinnedCertificate.Thumbprint);
    public static string KeyId => CertificateThumbprint;

    public static bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        try
        {
            using var rsa = PinnedCertificate.GetRSAPublicKey()
                            ?? throw new InvalidDataException("The pinned Opticon source-release certificate has no RSA public key.");
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static X509Certificate2 LoadPinnedCertificate()
    {
        if (!BuildSigningTrust.IsPublishable && string.IsNullOrWhiteSpace(BuildSigningTrust.SourceReleaseCertificateBase64))
            return X509CertificateLoader.LoadCertificate(InvitationSigning.PinnedCertificate.RawData);

        var certificate = LoadPublicCertificate(BuildSigningTrust.SourceReleaseCertificateBase64, "source-release");
        var actual = NormalizeThumbprint(certificate.Thumbprint);
        if (!actual.Equals(BuildSigningTrust.SourceReleaseKeyId, StringComparison.Ordinal))
            throw new InvalidOperationException("The embedded source-release certificate does not match OpticonSourceReleaseKeyId.");
        if (actual.Equals(InvitationSigning.CertificateThumbprint, StringComparison.Ordinal))
            throw new InvalidOperationException("The source-release certificate must be separate from the online invitation certificate.");
        return certificate;
    }

    internal static X509Certificate2 LoadPublicCertificate(string value, string purpose)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"The embedded Opticon {purpose} public certificate is missing.");
        try
        {
            var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(value));
            if (certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidOperationException($"The embedded Opticon {purpose} certificate unexpectedly contains a private key.");
            }
            return certificate;
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"The embedded Opticon {purpose} certificate is malformed.", exception);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException($"The embedded Opticon {purpose} certificate is invalid.", exception);
        }
    }

    internal static string NormalizeThumbprint(string? value) =>
        new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    internal static bool RawEquals(X509Certificate2 left, X509Certificate2 right) =>
        left.RawData.Length == right.RawData.Length
        && CryptographicOperations.FixedTimeEquals(left.RawData, right.RawData);
}
