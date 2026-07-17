using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using StayActive.EnrollmentBroker.Configuration;

namespace StayActive.EnrollmentBroker.Auth;

public static class EnrollmentBrokerAuthorization
{
    public static void AddPolicies(AuthorizationOptions options, EnrollmentBrokerSettings settings)
    {
        options.AddPolicy(EnrollmentBrokerSettings.EnrollmentWritePolicy, policy =>
            policy.RequireAuthenticatedUser().RequireAssertion(context =>
                HasScope(context.User, settings.EnrollmentWriteScope)
                && HasRole(context.User, settings.AdministratorRole)
                && EnrollmentBrokerIdentity.GetActorSubject(context.User) is not null));
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

public static class EnrollmentBrokerIdentity
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
