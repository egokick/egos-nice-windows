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

/// <summary>
/// The source bootstrap needs an SDK before it can build any Opticon code, so
/// its installer is pinned independently of MSI dependency artifacts. SHA-512
/// values are the official .NET release metadata digests for the exact stable
/// SDK release; a future SDK upgrade requires a signed Opticon source release.
/// </summary>
public sealed record DotNetSdkArtifact(
    string Version,
    string FileName,
    string Sha512,
    string Url);

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

    public static DotNetSdkArtifact DotNetSdk(Architecture architecture) => architecture switch
    {
        Architecture.Arm64 => new DotNetSdkArtifact(
            "10.0.302", "dotnet-sdk-10.0.302-win-arm64.exe",
            "79cb55d060123c8dbb017793ec760a9f76310c5c6c9475445a14cb43ac53940c89dfc505588540f69e0b62559bd3e2b98910b3319cd6c01c5ff29d248942cc95",
            "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.302/dotnet-sdk-10.0.302-win-arm64.exe"),
        Architecture.X64 => new DotNetSdkArtifact(
            "10.0.302", "dotnet-sdk-10.0.302-win-x64.exe",
            "ffa847d86755033a4e2c8dd19ab3b0d9c8ae129e1e59cef460f792cced6319c69f730b96e05c5bb88ba906094f332bf5232d4c417605789f03a310dd8f3d22c2",
            "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.302/dotnet-sdk-10.0.302-win-x64.exe"),
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
