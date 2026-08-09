using System.Reflection;

namespace Taildesk.Shared;

public enum OpticonSigningProfile
{
    Developer,
    OwnerManaged,
    Production
}

/// <summary>
/// Build-injected trust metadata. Private keys are never embedded; source
/// packages carry only the public trust roots needed by the binaries they build.
/// </summary>
public static class BuildSigningTrust
{
    private static readonly IReadOnlyDictionary<string, string> Metadata =
        typeof(BuildSigningTrust).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(attribute => attribute.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value ?? string.Empty, StringComparer.Ordinal);

    public static OpticonSigningProfile Profile { get; } = ParseProfile(Get("OpticonSigningProfile"));
    public static bool IsProduction => Profile == OpticonSigningProfile.Production;
    public static bool IsOwnerManaged => Profile == OpticonSigningProfile.OwnerManaged;
    public static bool IsPublishable => IsProduction || IsOwnerManaged;
    public static string ProfileName => Profile.ToString();

    internal static string SourceReleaseKeyId => Get("OpticonSourceReleaseKeyId").Trim().ToUpperInvariant();
    internal static string SourceReleaseCertificateBase64 => Get("OpticonSourceReleaseCertificateBase64").Trim();
    internal static string ProductSignerThumbprint => Get("OpticonProductSignerThumbprint").Trim().ToUpperInvariant();
    internal static string ProductSigningCertificateBase64 => Get("OpticonProductSigningCertificateBase64").Trim();

    public static void RequirePublishable()
    {
        if (!IsPublishable)
            throw new InvalidOperationException(
                "Developer-signed Opticon artifacts are intentionally not publishable. Rebuild with OpticonSigningProfile=Production or OwnerManaged and separate release/code-signing certificates.");
        _ = SourceReleaseSigning.PinnedCertificate;
        _ = ProductSigning.PinnedCertificate;
    }

    private static string Get(string key) => Metadata.TryGetValue(key, out var value) ? value : string.Empty;

    private static OpticonSigningProfile ParseProfile(string value) => value switch
    {
        "Developer" => OpticonSigningProfile.Developer,
        "OwnerManaged" => OpticonSigningProfile.OwnerManaged,
        "Production" => OpticonSigningProfile.Production,
        _ => throw new InvalidOperationException("The embedded Opticon signing profile is invalid.")
    };
}
