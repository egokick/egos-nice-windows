namespace Taildesk.Shared;

public static class InvitationPolicy
{
    public const int LegacyBundleSchemaVersion = 2;
    public const int HostedLinkSchemaVersion = 3;
    public const int DefaultLifetimeDays = 14;
    public const int MaximumLifetimeDays = 365;

    public static bool IsSupportedPayloadSchema(int schemaVersion) =>
        schemaVersion is LegacyBundleSchemaVersion or HostedLinkSchemaVersion;

    public static DateTimeOffset CreateDefaultExpiry() =>
        DateTimeOffset.UtcNow.AddDays(DefaultLifetimeDays);
}
