using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Taildesk.Shared;

/// <summary>
/// Public-CA Authenticode trust for executable product payloads. Unsigned local
/// source builds are accepted only through SourceBuildProvenance's protected,
/// exact path/size/hash record.
/// </summary>
public static class ProductSigning
{
    private const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";

    public static X509Certificate2 PinnedCertificate { get; } = LoadPinnedCertificate();
    public static string CertificateThumbprint { get; } = SourceReleaseSigning.NormalizeThumbprint(PinnedCertificate.Thumbprint);

    public static async Task VerifyAuthenticodeAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await BoundWindowsProductSignatureVerifier.VerifyPinnedAsync(
                path,
                PinnedCertificate,
                requireWindowsTrustedChain: BuildSigningTrust.IsProduction,
                requireRfc3161Timestamp: BuildSigningTrust.IsPublishable,
                cancellationToken);
        }
        catch (InvalidDataException)
        {
            if (BuildSigningTrust.IsLegacyMigrationBuild)
            {
                try
                {
                    // Devices on the retired 1.1.38 trust root can accept only
                    // this exact, one-version migration package. The binary
                    // immediately changes their update-manifest trust to the
                    // independent offline source-release key.
                    await BoundWindowsProductSignatureVerifier.VerifyPinnedAsync(
                        path,
                        InvitationSigning.PinnedCertificate,
                        requireWindowsTrustedChain: false,
                        requireRfc3161Timestamp: true,
                        cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    return;
                }
                catch (InvalidDataException) when (SourceBuildProvenance.TryVerify(path))
                {
                    return;
                }
            }

            if (SourceBuildProvenance.TryVerify(path))
            {
                // The protected provenance store is the sole exception for
                // locally source-built binaries. It binds this exact path,
                // size, and hash to a verified source archive and invitation.
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public static void ValidateCodeSigningCertificate(X509Certificate2 certificate, bool requirePublicChain)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        if (eku.Length == 0 || !eku.SelectMany(extension => extension.EnhancedKeyUsages.Cast<Oid>())
                .Any(oid => oid.Value == CodeSigningEku))
            throw new InvalidOperationException("The Opticon product signer lacks the Code Signing EKU.");
        if (requirePublicChain && certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
            throw new InvalidOperationException("The production Opticon product signer must not be self-signed.");
    }

    private static X509Certificate2 LoadPinnedCertificate()
    {
        X509Certificate2 certificate;
        if (!BuildSigningTrust.IsPublishable && string.IsNullOrWhiteSpace(BuildSigningTrust.ProductSigningCertificateBase64))
        {
            certificate = X509CertificateLoader.LoadCertificate(InvitationSigning.PinnedCertificate.RawData);
        }
        else
        {
            certificate = SourceReleaseSigning.LoadPublicCertificate(
                BuildSigningTrust.ProductSigningCertificateBase64, "product-signing");
            var actual = SourceReleaseSigning.NormalizeThumbprint(certificate.Thumbprint);
            if (!actual.Equals(BuildSigningTrust.ProductSignerThumbprint, StringComparison.Ordinal))
                throw new InvalidOperationException("The embedded product-signing certificate does not match OpticonProductSignerThumbprint.");
            if (actual.Equals(InvitationSigning.CertificateThumbprint, StringComparison.Ordinal))
                throw new InvalidOperationException("The product signer must be separate from the online invitation certificate.");
            if (actual.Equals(SourceReleaseSigning.CertificateThumbprint, StringComparison.Ordinal))
                throw new InvalidOperationException("The product signer must be separate from the offline source-release certificate.");
        }

        ValidateCodeSigningCertificate(certificate, BuildSigningTrust.IsProduction);
        return certificate;
    }
}
