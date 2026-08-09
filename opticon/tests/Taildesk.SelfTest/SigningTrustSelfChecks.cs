using System.Runtime.CompilerServices;
using Taildesk.Shared;

namespace Taildesk.SelfTest;

internal static class SigningTrustSelfChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        if (SourceReleaseSigning.Verify([0x01, 0x02], [0x03, 0x04]))
            throw new InvalidOperationException("Source-release verification accepted a malformed RSA-PSS signature.");

        ProductSigning.ValidateCodeSigningCertificate(
            ProductSigning.PinnedCertificate,
            requirePublicChain: BuildSigningTrust.IsProduction);

        if (BuildSigningTrust.IsPublishable)
        {
            BuildSigningTrust.RequirePublishable();
            if (SourceReleaseSigning.CertificateThumbprint.Equals(
                    InvitationSigning.CertificateThumbprint, StringComparison.OrdinalIgnoreCase)
                || ProductSigning.CertificateThumbprint.Equals(
                    InvitationSigning.CertificateThumbprint, StringComparison.OrdinalIgnoreCase)
                || ProductSigning.CertificateThumbprint.Equals(
                    SourceReleaseSigning.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Publishable signing trust domains are not distinct.");
        }
        else
        {
            if (BuildSigningTrust.IsPublishable)
                throw new InvalidOperationException("A developer signing profile was marked publishable.");
            try
            {
                BuildSigningTrust.RequirePublishable();
                throw new InvalidOperationException("A developer build passed the production publication gate.");
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("not publishable", StringComparison.OrdinalIgnoreCase))
            {
            }
        }
    }
}
