using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using StayActive.RemoteHub.Configuration;

namespace StayActive.RemoteHub.Auth;

public static class RemoteHubAuthorization
{
    public static void AddPolicies(AuthorizationOptions options, RemoteHubSettings settings)
    {
        options.AddPolicy(RemoteHubSettings.FleetReadPolicy, policy =>
            policy.RequireAuthenticatedUser().RequireAssertion(context =>
                HasScope(context.User, settings.FleetReadScope) && RemoteHubIdentity.GetActorSubject(context.User) is not null));

        options.AddPolicy(RemoteHubSettings.InventoryWritePolicy, policy =>
            policy.RequireAuthenticatedUser().RequireAssertion(context =>
                HasScope(context.User, settings.InventoryWriteScope)
                && HasRole(context.User, settings.AdministratorRole)
                && RemoteHubIdentity.GetActorSubject(context.User) is not null));

        options.AddPolicy(RemoteHubSettings.AuditReadPolicy, policy =>
            policy.RequireAuthenticatedUser().RequireAssertion(context =>
                (HasScope(context.User, settings.AuditReadScope) || HasScope(context.User, settings.InventoryWriteScope))
                && HasRole(context.User, settings.AdministratorRole)
                && RemoteHubIdentity.GetActorSubject(context.User) is not null));
    }

    public static bool HasScope(ClaimsPrincipal user, string requiredScope) =>
        user.Claims
            .Where(static claim => claim.Type is "scope" or "scp")
            .SelectMany(static claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(scope => string.Equals(scope, requiredScope, StringComparison.Ordinal));

    public static bool HasRole(ClaimsPrincipal user, string requiredRole) =>
        user.Claims
            .Where(static claim => claim.Type is "role" or ClaimTypes.Role)
            .SelectMany(static claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(role => string.Equals(role, requiredRole, StringComparison.Ordinal));
}

public static class RemoteHubIdentity
{
    private static readonly string[] ActorClaimTypes = ["sub", "client_id", ClaimTypes.NameIdentifier];

    public static string? GetActorSubject(ClaimsPrincipal user)
    {
        foreach (var claimType in ActorClaimTypes)
        {
            var candidate = user.FindFirst(claimType)?.Value?.Trim();
            if (candidate is not null && IsSafeSubject(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool IsSafeSubject(string value) =>
        value.Length is > 0 and <= 256 && value.All(static character => !char.IsControl(character));
}
