namespace Taildesk.Shared;

public static class InvitationPolicy
{
    public const int LegacyBundleSchemaVersion = 2;
    public const int PreviousHostedLinkSchemaVersion = 3;
    public const int PreviousSourceBuildSchemaVersion = 4;
    public const int PreviousBootstrapPinnedSourceBuildSchemaVersion = 5;
    public const int HostedLinkSchemaVersion = 6;
    public const string SourceInstallProtocol = "source-v1";
    public const string BinaryInstallProtocol = "binary-v1";
    public const int DefaultLifetimeDays = 14;
    public const int MaximumLifetimeDays = 365;

    public static bool IsSupportedPayloadSchema(int schemaVersion) =>
        schemaVersion is LegacyBundleSchemaVersion or PreviousHostedLinkSchemaVersion
            or PreviousSourceBuildSchemaVersion or PreviousBootstrapPinnedSourceBuildSchemaVersion
            or HostedLinkSchemaVersion;

    public static bool IsInstallablePayloadSchema(int schemaVersion) => schemaVersion == HostedLinkSchemaVersion;

    public static DateTimeOffset CreateDefaultExpiry() =>
        DateTimeOffset.UtcNow.AddDays(DefaultLifetimeDays);
}
