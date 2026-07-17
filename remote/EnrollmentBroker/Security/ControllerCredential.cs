namespace StayActive.EnrollmentBroker.Security;

/// <summary>
/// Defines the only credential location accepted by the broker. Keeping the
/// target name out of normal configuration prevents a deployment value from
/// redirecting the service to an arbitrary credential.
/// </summary>
public static class ControllerCredential
{
    public const string TargetName = "StayActive/HeadscaleController/v1";
    private const string HeadscaleApiKeyPrefix = "hskey-api-";

    public static string Validate(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 1024
            || normalized.Any(char.IsWhiteSpace)
            || normalized.Any(char.IsControl)
            || !normalized.StartsWith(HeadscaleApiKeyPrefix, StringComparison.Ordinal)
            || normalized.Length == HeadscaleApiKeyPrefix.Length)
        {
            throw new ControllerCredentialException("The controller credential is missing or invalid.");
        }

        return normalized;
    }
}

public sealed class ControllerCredentialException : Exception
{
    public ControllerCredentialException(string message)
        : base(message)
    {
    }
}
