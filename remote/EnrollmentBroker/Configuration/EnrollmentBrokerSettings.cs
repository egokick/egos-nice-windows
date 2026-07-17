using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace StayActive.EnrollmentBroker.Configuration;

/// <summary>
/// Parses all operational security boundaries before any endpoint is mapped.
/// Production has no development-auth fallback and accepts no controller credential
/// from configuration, environment variables, command-line arguments, or files.
/// Its journal HMAC key remains file-backed to avoid ordinary configuration dumps.
/// </summary>
public sealed class EnrollmentBrokerSettings
{
    private readonly byte[] _journalHmacKey;

    internal EnrollmentBrokerSettings(
        bool localDevelopmentEnabled,
        int localDevelopmentPort,
        string? oidcAuthority,
        string? oidcAudience,
        string enrollmentWriteScope,
        string administratorRole,
        string journalPath,
        byte[] journalHmacKey,
        Uri headscaleApiBaseUri,
        string headscaleUserId,
        string headscaleLoginServer)
    {
        LocalDevelopmentEnabled = localDevelopmentEnabled;
        LocalDevelopmentPort = localDevelopmentPort;
        OidcAuthority = oidcAuthority;
        OidcAudience = oidcAudience;
        EnrollmentWriteScope = enrollmentWriteScope;
        AdministratorRole = administratorRole;
        JournalPath = journalPath;
        _journalHmacKey = journalHmacKey.ToArray();
        HeadscaleApiBaseUri = headscaleApiBaseUri;
        HeadscaleUserId = headscaleUserId;
        HeadscaleLoginServer = headscaleLoginServer;
    }

    public bool LocalDevelopmentEnabled { get; }

    public int LocalDevelopmentPort { get; }

    public string? OidcAuthority { get; }

    public string? OidcAudience { get; }

    public string EnrollmentWriteScope { get; }

    public string AdministratorRole { get; }

    public string JournalPath { get; }

    public Uri HeadscaleApiBaseUri { get; }

    public string HeadscaleUserId { get; }

    public string HeadscaleLoginServer { get; }


    internal byte[] CreateJournalHmacKeyCopy() => _journalHmacKey.ToArray();

    public override string ToString() =>
        "EnrollmentBrokerSettings { JournalHmacKey = [REDACTED] }";

    public const string HeadscaleControllerApiBaseUrl = "https://headscale-controller.stayactive.test:4443/";
    public const string EnrollmentWritePolicy = "enrollmentbroker.ticket.write";
    public const string TicketCreationRateLimitPolicy = "enrollmentbroker.ticket.create";

    public static EnrollmentBrokerSettings Load(IConfiguration configuration, IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var localDevelopmentEnabled = ReadBoolean(configuration, "EnrollmentBroker:LocalDevelopment:Enabled");
        if (localDevelopmentEnabled && environment.IsProduction())
        {
            throw new InvalidOperationException("EnrollmentBroker local development authentication is forbidden in Production.");
        }

        if (localDevelopmentEnabled && !environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException(
                "EnrollmentBroker local development authentication is only allowed in Development or Testing.");
        }

        var authority = Normalize(configuration["EnrollmentBroker:Authentication:Authority"]);
        var audience = Normalize(configuration["EnrollmentBroker:Authentication:Audience"]);
        if (!localDevelopmentEnabled)
        {
            ValidateHttpsIssuer(authority, "EnrollmentBroker:Authentication:Authority");
            if (!IsSafeScalar(audience, 255))
            {
                throw new InvalidOperationException(
                    "EnrollmentBroker:Authentication:Audience is required and must be a printable value of at most 255 characters.");
            }
        }

        var localDevelopmentPort = ReadInt(configuration, "EnrollmentBroker:LocalDevelopment:Port", 5090);
        if (localDevelopmentPort is < 1024 or > 65535)
        {
            throw new InvalidOperationException("EnrollmentBroker:LocalDevelopment:Port must be between 1024 and 65535.");
        }

        var journalPath = Normalize(configuration["EnrollmentBroker:Storage:JournalPath"]);
        if (journalPath.Length == 0)
        {
            throw new InvalidOperationException("EnrollmentBroker:Storage:JournalPath is required.");
        }

        if (environment.IsProduction() && !Path.IsPathFullyQualified(journalPath))
        {
            throw new InvalidOperationException("EnrollmentBroker:Storage:JournalPath must be absolute in Production.");
        }

        var fullJournalPath = Path.GetFullPath(journalPath);
        if (string.IsNullOrWhiteSpace(Path.GetDirectoryName(fullJournalPath)))
        {
            throw new InvalidOperationException("EnrollmentBroker:Storage:JournalPath must name a file in a directory.");
        }

        var journalHmacKey = ReadSecret(
            configuration,
            "EnrollmentBroker:Storage:JournalHmacKeyFile",
            "EnrollmentBroker:Storage:JournalHmacKey",
            environment,
            purpose: "journal HMAC key");
        var decodedHmacKey = DecodeHmacKey(journalHmacKey);

        var headscaleApiBase = Normalize(configuration["EnrollmentBroker:Headscale:ApiBaseUrl"]);
        var headscaleApiBaseUri = ValidateFixedHeadscaleControllerApiBaseUrl(headscaleApiBase);
        RejectLegacyHeadscaleApiKeyConfiguration(configuration);

        var headscaleUserId = Normalize(configuration["EnrollmentBroker:Headscale:UserId"]);
        if (headscaleUserId.Length is 0 or > 20
            || !headscaleUserId.All(static character => character is >= '0' and <= '9')
            || headscaleUserId.All(static character => character == '0'))
        {
            throw new InvalidOperationException("EnrollmentBroker:Headscale:UserId must be a positive Headscale numeric user id.");
        }

        var headscaleLoginServer = Normalize(configuration["EnrollmentBroker:Headscale:LoginServer"]);
        var headscaleLoginServerUri = ValidateHttpsOrigin(headscaleLoginServer, "EnrollmentBroker:Headscale:LoginServer");

        var enrollmentWriteScope = ReadScope(
            configuration,
            "EnrollmentBroker:Authorization:EnrollmentWriteScope",
            "stayactive.enrollment.write");
        var administratorRole = ReadRole(
            configuration,
            "EnrollmentBroker:Authorization:AdministratorRole",
            "stayactive.enrollment.admin");

        var settings = new EnrollmentBrokerSettings(
            localDevelopmentEnabled,
            localDevelopmentPort,
            authority.Length == 0 ? null : authority.TrimEnd('/'),
            audience.Length == 0 ? null : audience,
            enrollmentWriteScope,
            administratorRole,
            fullJournalPath,
            decodedHmacKey,
            headscaleApiBaseUri,
            headscaleUserId,
            CanonicalOrigin(headscaleLoginServerUri));
        CryptographicOperations.ZeroMemory(decodedHmacKey);
        return settings;
    }

    private static void ValidateHttpsIssuer(string candidate, string settingName)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException($"{settingName} must be an absolute HTTPS OIDC issuer with no query, fragment, or user info.");
        }
    }

    private static Uri ValidateFixedHeadscaleControllerApiBaseUrl(string candidate)
    {
        if (!string.Equals(candidate, HeadscaleControllerApiBaseUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"EnrollmentBroker:Headscale:ApiBaseUrl must be exactly {HeadscaleControllerApiBaseUrl}.");
        }

        return new Uri(HeadscaleControllerApiBaseUrl, UriKind.Absolute);
    }

    private static Uri ValidateHttpsOrigin(string candidate, string settingName)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException($"{settingName} must be an absolute HTTPS origin with no path, query, fragment, or user info.");
        }

        return uri;
    }

    private static string CanonicalOrigin(Uri uri) => uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');

    private static void RejectLegacyHeadscaleApiKeyConfiguration(IConfiguration configuration)
    {
        foreach (var key in new[]
                 {
                     "EnrollmentBroker:Headscale:ApiKeyFile",
                     "EnrollmentBroker:Headscale:ApiKey"
                 })
        {
            if (Normalize(configuration[key]).Length > 0)
            {
                throw new InvalidOperationException(
                    $"{key} is no longer supported. Store the controller key in Windows Credential Manager instead.");
            }
        }
    }

    private static string ReadSecret(
        IConfiguration configuration,
        string fileKey,
        string? inlineKey,
        IWebHostEnvironment environment,
        string purpose)
    {
        var path = Normalize(configuration[fileKey]);
        if (path.Length > 0)
        {
            if (environment.IsProduction() && !Path.IsPathFullyQualified(path))
            {
                throw new InvalidOperationException($"{fileKey} must be an absolute path in Production.");
            }

            try
            {
                return File.ReadAllText(path).Trim();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Unable to read {purpose} from {fileKey}.", exception);
            }
        }

        if (!environment.IsProduction() && inlineKey is not null)
        {
            return Normalize(configuration[inlineKey]);
        }

        throw new InvalidOperationException($"{fileKey} is required for the EnrollmentBroker {purpose}.");
    }

    private static byte[] DecodeHmacKey(string encoded)
    {
        try
        {
            var key = Convert.FromBase64String(encoded);
            if (key.Length < 32)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new InvalidOperationException("EnrollmentBroker journal HMAC key must decode to at least 32 bytes.");
            }

            return key;
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("EnrollmentBroker journal HMAC key must be Base64 encoded.", exception);
        }
    }

    private static bool ReadBoolean(IConfiguration configuration, string key)
    {
        var value = Normalize(configuration[key]);
        if (value.Length == 0)
        {
            return false;
        }

        return bool.TryParse(value, out var result)
            ? result
            : throw new InvalidOperationException($"{key} must be true or false.");
    }

    private static int ReadInt(IConfiguration configuration, string key, int defaultValue)
    {
        var value = Normalize(configuration[key]);
        if (value.Length == 0)
        {
            return defaultValue;
        }

        return int.TryParse(value, out var result)
            ? result
            : throw new InvalidOperationException($"{key} must be an integer.");
    }

    private static string ReadScope(IConfiguration configuration, string key, string defaultValue)
    {
        var value = Normalize(configuration[key]);
        value = value.Length == 0 ? defaultValue : value;
        if (!IsSafeScalar(value, 128) || value.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException($"{key} must be one non-whitespace scope value of at most 128 characters.");
        }

        return value;
    }

    private static string ReadRole(IConfiguration configuration, string key, string defaultValue)
    {
        var value = Normalize(configuration[key]);
        value = value.Length == 0 ? defaultValue : value;
        if (!IsSafeScalar(value, 128) || value.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException($"{key} must be one non-whitespace role value of at most 128 characters.");
        }

        return value;
    }

    private static bool IsSafeScalar(string? value, int maximumLength) =>
        value is { Length: > 0 }
        && value.Length <= maximumLength
        && value.All(static character => !char.IsControl(character));


    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}
