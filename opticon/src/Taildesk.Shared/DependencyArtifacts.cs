using System.Runtime.InteropServices;

namespace Taildesk.Shared;

public sealed record DependencyArtifact(
    string Product,
    string Version,
    string FileName,
    string Sha256,
    string ExpectedSignerThumbprint,
    long Size,
    string PrimaryUrl,
    string FallbackUrl);

public static class DependencyArtifacts
{
    public const string FlyArtifactBase = "https://taildesk-egokick-control.fly.dev/opticon/artifacts/v1/";

    public static DependencyArtifact Tailscale(Architecture architecture) => architecture switch
    {
        Architecture.Arm64 => Create("Tailscale", "1.102.1", "tailscale-setup-1.102.1-arm64.msi",
            "f81002c5b971fe2de197703606e81107eacc83c6ea40478976fe5de154aed177",
            "108F172FDE945B21A5C0696731D6220D67D1C39E", 36000256,
            "https://pkgs.tailscale.com/stable/tailscale-setup-1.102.1-arm64.msi"),
        Architecture.X64 => Create("Tailscale", "1.102.1", "tailscale-setup-1.102.1-amd64.msi",
            "988a38ab854ad176778955b0c92b27b1af14bf5e0146ea43076d829496d7ac77",
            "108F172FDE945B21A5C0696731D6220D67D1C39E", 38354432,
            "https://pkgs.tailscale.com/stable/tailscale-setup-1.102.1-amd64.msi"),
        _ => throw new PlatformNotSupportedException("Opticon supports only Windows x64 and ARM64.")
    };

    public static DependencyArtifact RustDesk(Architecture architecture) => architecture switch
    {
        Architecture.Arm64 => Create("RustDesk", "1.4.9", "rustdesk-1.4.9-aarch64.msi",
            "30bc8925e62c7ade52371758c2b944036ed2386f6c554e9e59f3bcfef06c7cd9",
            "4230334F8A7DD84E50D0273EF379E8B4A82F5DA5", 22855680,
            "https://github.com/rustdesk/rustdesk/releases/download/1.4.9/rustdesk-1.4.9-aarch64.msi"),
        Architecture.X64 => Create("RustDesk", "1.4.9", "rustdesk-1.4.9-x86_64.msi",
            "c87d2f4cef2a5acd6003b6507dcfbf5d5168a256db082cd90b54d35193224aaa",
            "4230334F8A7DD84E50D0273EF379E8B4A82F5DA5", 24825856,
            "https://github.com/rustdesk/rustdesk/releases/download/1.4.9/rustdesk-1.4.9-x86_64.msi"),
        _ => throw new PlatformNotSupportedException("Opticon supports only Windows x64 and ARM64.")
    };

    public static IReadOnlyList<DependencyArtifact> All { get; } =
    [
        Tailscale(Architecture.X64), Tailscale(Architecture.Arm64),
        RustDesk(Architecture.X64), RustDesk(Architecture.Arm64)
    ];

    private static DependencyArtifact Create(
        string product,
        string version,
        string fileName,
        string sha256,
        string expectedSignerThumbprint,
        long size,
        string fallback) =>
        new(product, version, fileName, sha256, expectedSignerThumbprint, size, FlyArtifactBase + fileName, fallback);
}
