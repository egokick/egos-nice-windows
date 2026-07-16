using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace StayActive.RemoteHub.Configuration;

/// <summary>
/// Configuration is parsed once on startup. In particular, there is no fallback
/// authentication mode: an accidental missing OIDC value stops the service.
/// </summary>
public sealed record RemoteHubSettings(
    bool LocalDevelopmentEnabled,
    int LocalDevelopmentPort,
    string? OidcAuthority,
    string? OidcAudience,
    AdminSpaSettings? AdminSpa,
    string JournalPath,
    byte[] JournalHmacKey,
    string FleetReadScope,
    string InventoryWriteScope,
    string AuditReadScope,
    string AdministratorRole,
    string AdministratorRoleScope)
{
    public const string FleetReadPolicy = "remotehub.fleet.read";
    public const string InventoryWritePolicy = "remotehub.inventory.write";
    public const string AuditReadPolicy = "remotehub.audit.read";

    public static RemoteHubSettings Load(IConfiguration configuration, IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var developmentEnabled = ReadBoolean(configuration, "RemoteHub:LocalDevelopment:Enabled");
        if (developmentEnabled && environment.IsProduction())
        {
            throw new InvalidOperationException(
                "RemoteHub local development authentication is forbidden in Production.");
        }

        if (developmentEnabled && !environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException(
                "RemoteHub local development authentication is only allowed in Development or Testing.");
        }

        var authority = Normalize(configuration["RemoteHub:Authentication:Authority"]);
        var audience = Normalize(configuration["RemoteHub:Authentication:Audience"]);
        if (!developmentEnabled)
        {
            if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri)
                || authorityUri.Scheme != Uri.UriSchemeHttps
                || string.IsNullOrEmpty(authorityUri.Host)
                || !string.IsNullOrEmpty(authorityUri.UserInfo)
                || !string.IsNullOrEmpty(authorityUri.Query)
                || !string.IsNullOrEmpty(authorityUri.Fragment))
            {
                throw new InvalidOperationException(
                    "RemoteHub:Authentication:Authority must be an absolute HTTPS OIDC issuer when local development mode is disabled.");
            }

            if (audience.Length is 0 or > 255 || audience.Any(char.IsControl))
            {
                throw new InvalidOperationException(
                    "RemoteHub:Authentication:Audience is required and must be a printable value of at most 255 characters.");
            }
        }

        var journalPath = Normalize(configuration["RemoteHub:Storage:JournalPath"]);
        if (journalPath.Length == 0)
        {
            throw new InvalidOperationException("RemoteHub:Storage:JournalPath is required.");
        }

        if (environment.IsProduction() && !Path.IsPathFullyQualified(journalPath))
        {
            throw new InvalidOperationException("RemoteHub:Storage:JournalPath must be absolute in Production.");
        }

        if (string.IsNullOrWhiteSpace(Path.GetDirectoryName(Path.GetFullPath(journalPath))))
        {
            throw new InvalidOperationException("RemoteHub:Storage:JournalPath must name a file in a directory.");
        }

        var hmacKey = DecodeHmacKey(configuration["RemoteHub:Storage:JournalHmacKey"]);

        var localPort = ReadInt(configuration, "RemoteHub:LocalDevelopment:Port", 5097);
        if (localPort is < 1024 or > 65535)
        {
            throw new InvalidOperationException("RemoteHub:LocalDevelopment:Port must be between 1024 and 65535.");
        }

        var fleetReadScope = ReadScope(configuration, "RemoteHub:Authorization:FleetReadScope", "remotehub.fleet.read");
        var inventoryWriteScope = ReadScope(configuration, "RemoteHub:Authorization:InventoryWriteScope", "remotehub.inventory.write");
        var auditReadScope = ReadScope(configuration, "RemoteHub:Authorization:AuditReadScope", "remotehub.audit.read");
        var administratorRole = ReadRole(
            configuration,
            "RemoteHub:Authorization:AdministratorRole",
            "stayactive.remotehub.admin");
        var administratorRoleScope = ReadScope(
            configuration,
            "RemoteHub:Authorization:AdministratorRoleScope",
            "remotehub.admin");
        var adminSpa = LoadAdminSpaSettings(
            configuration,
            environment,
            developmentEnabled,
            authority,
            inventoryWriteScope,
            auditReadScope,
            administratorRoleScope);

        return new RemoteHubSettings(
            developmentEnabled,
            localPort,
            authority.Length == 0 ? null : authority.TrimEnd('/'),
            audience.Length == 0 ? null : audience,
            adminSpa,
            Path.GetFullPath(journalPath),
            hmacKey,
            fleetReadScope,
            inventoryWriteScope,
            auditReadScope,
            administratorRole,
            administratorRoleScope);
    }

    private static AdminSpaSettings? LoadAdminSpaSettings(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        bool localDevelopmentEnabled,
        string authority,
        string inventoryWriteScope,
        string auditReadScope,
        string administratorRoleScope)
    {
        if (!ReadBoolean(configuration, "RemoteHub:AdminSpa:Enabled"))
        {
            return null;
        }

        if (localDevelopmentEnabled)
        {
            throw new InvalidOperationException(
                "RemoteHub Admin SPA requires OIDC JWT authentication and cannot be enabled with local development header authentication.");
        }

        if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri)
            || authorityUri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(authorityUri.Host))
        {
            throw new InvalidOperationException(
                "RemoteHub Admin SPA requires RemoteHub:Authentication:Authority to be an absolute HTTPS OIDC issuer.");
        }

        var publicOrigin = Normalize(configuration["RemoteHub:AdminSpa:PublicOrigin"]);
        if (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out var publicOriginUri)
            || string.IsNullOrEmpty(publicOriginUri.Host)
            || !string.IsNullOrEmpty(publicOriginUri.UserInfo)
            || !string.IsNullOrEmpty(publicOriginUri.Query)
            || !string.IsNullOrEmpty(publicOriginUri.Fragment)
            || publicOriginUri.AbsolutePath != "/")
        {
            throw new InvalidOperationException(
                "RemoteHub:AdminSpa:PublicOrigin must be an origin with no path, query, fragment, or user info.");
        }

        if (environment.IsProduction() && publicOriginUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("RemoteHub Admin SPA requires an HTTPS PublicOrigin in Production.");
        }

        if (!environment.IsProduction()
            && publicOriginUri.Scheme == Uri.UriSchemeHttp
            && !IsLoopbackHost(publicOriginUri.Host))
        {
            throw new InvalidOperationException(
                "HTTP RemoteHub Admin SPA origins are allowed only for loopback Development/Testing use.");
        }

        if (!string.Equals(publicOriginUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(publicOriginUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("RemoteHub:AdminSpa:PublicOrigin must use HTTPS or loopback HTTP.");
        }

        var clientId = Normalize(configuration["RemoteHub:AdminSpa:ClientId"]);
        if (clientId.Length is 0 or > 255 || clientId.Any(char.IsWhiteSpace) || clientId.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "RemoteHub:AdminSpa:ClientId is required and must be a non-whitespace public client identifier of at most 255 characters.");
        }

        var scopes = ParseAdminSpaScopes(
            configuration["RemoteHub:AdminSpa:Scopes"],
            inventoryWriteScope,
            auditReadScope,
            administratorRoleScope);
        var normalizedOrigin = publicOriginUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return new AdminSpaSettings(
            authorityUri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
            clientId,
            normalizedOrigin + "/admin/",
            scopes);
    }

    private static string[] ParseAdminSpaScopes(
        string? rawValue,
        string inventoryWriteScope,
        string auditReadScope,
        string administratorRoleScope)
    {
        var scopes = string.IsNullOrWhiteSpace(rawValue)
            ? ["openid", "profile", inventoryWriteScope, auditReadScope, administratorRoleScope]
            : rawValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (scopes.Length == 0
            || scopes.Length > 16
            || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Length
            || scopes.Any(scope => scope.Length is 0 or > 128 || scope.Any(char.IsControl) || scope.Any(char.IsWhiteSpace)))
        {
            throw new InvalidOperationException("RemoteHub:AdminSpa:Scopes must be a short, distinct space-delimited scope list.");
        }

        if (!scopes.Contains("openid", StringComparer.Ordinal)
            || !scopes.Contains(inventoryWriteScope, StringComparer.Ordinal)
            || !scopes.Contains(administratorRoleScope, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "RemoteHub:AdminSpa:Scopes must include openid, the configured inventory-write scope, and the administrator-role scope.");
        }

        if (scopes.Contains("offline_access", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "RemoteHub Admin SPA does not request refresh tokens; offline_access is forbidden.");
        }

        return scopes;
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    private static bool ReadBoolean(IConfiguration configuration, string key)
    {
        var raw = configuration[key];
        return bool.TryParse(raw, out var value) && value;
    }

    private static int ReadInt(IConfiguration configuration, string key, int defaultValue)
    {
        var raw = configuration[key];
        return raw is null ? defaultValue : int.TryParse(raw, out var value) ? value : -1;
    }

    private static string ReadScope(IConfiguration configuration, string key, string defaultValue)
    {
        var scope = Normalize(configuration[key]);
        if (scope.Length == 0)
        {
            scope = defaultValue;
        }

        if (scope.Length > 128 || scope.Any(char.IsWhiteSpace) || scope.Any(char.IsControl))
        {
            throw new InvalidOperationException($"{key} must contain one non-whitespace scope value.");
        }

        return scope;
    }

    private static string ReadRole(IConfiguration configuration, string key, string defaultValue)
    {
        var role = Normalize(configuration[key]);
        if (role.Length == 0)
        {
            role = defaultValue;
        }

        if (role.Length > 128 || role.Any(char.IsWhiteSpace) || role.Any(char.IsControl))
        {
            throw new InvalidOperationException($"{key} must contain one non-whitespace role value.");
        }

        return role;
    }

    private static byte[] DecodeHmacKey(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            throw new InvalidOperationException(
                "RemoteHub:Storage:JournalHmacKey is required; supply a base64-encoded random key from a secret manager.");
        }

        try
        {
            var key = Convert.FromBase64String(rawValue.Trim());
            if (key.Length < 32)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new InvalidOperationException(
                    "RemoteHub:Storage:JournalHmacKey must decode to at least 32 random bytes.");
            }

            if (key.All(static value => value == 0))
            {
                CryptographicOperations.ZeroMemory(key);
                throw new InvalidOperationException(
                    "RemoteHub:Storage:JournalHmacKey must not be an all-zero key.");
            }

            return key;
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "RemoteHub:Storage:JournalHmacKey must be valid base64.",
                exception);
        }
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}

/// <summary>
/// Values intentionally safe to disclose to the browser. This is a public OIDC
/// client: it has no client secret and never receives the journal HMAC key.
/// </summary>
public sealed record AdminSpaSettings(
    string Authority,
    string ClientId,
    string RedirectUri,
    string[] Scopes)
{
    public AdminSpaPublicConfiguration ToPublicConfiguration() =>
        new(Authority, ClientId, RedirectUri, Scopes.ToArray());
}

public sealed record AdminSpaPublicConfiguration(
    string Authority,
    string ClientId,
    string RedirectUri,
    string[] Scopes);
