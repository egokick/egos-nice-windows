using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace StayActive.RemoteHub.Auth;

/// <summary>
/// An intentionally narrow convenience for loopback-only development and test
/// runs. It is never registered in Production (startup throws before that can
/// happen). It does not use or accept passwords.
/// </summary>
public sealed class DevelopmentHeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "RemoteHubLocalDevelopment";
    public const string SubjectHeader = "X-RemoteHub-Dev-Subject";
    public const string ScopesHeader = "X-RemoteHub-Dev-Scopes";
    public const string RolesHeader = "X-RemoteHub-Dev-Roles";

    private readonly IHostEnvironment _environment;

    public DevelopmentHeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IHostEnvironment environment)
        : base(options, logger, encoder)
    {
        _environment = environment;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // In Development, Kestrel is explicitly bound to loopback. This check
        // prevents accidental use through an alternate listener as well.
        if (!_environment.IsEnvironment("Testing"))
        {
            var remoteAddress = Context.Connection.RemoteIpAddress;
            if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
            {
                return Task.FromResult(AuthenticateResult.Fail("Local development authentication only accepts loopback requests."));
            }
        }

        var subject = Request.Headers[SubjectHeader].ToString().Trim();
        var scopes = Request.Headers[ScopesHeader].ToString().Trim();
        var roles = Request.Headers[RolesHeader].ToString().Trim();
        if (subject.Length == 0 || scopes.Length == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!RemoteHubIdentity.IsSafeSubject(subject))
        {
            return Task.FromResult(AuthenticateResult.Fail("Development subject is invalid."));
        }

        var claims = new List<Claim>
        {
            new("sub", subject),
            new("scope", scopes)
        };
        foreach (var role in roles.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (role.Length <= 128 && !role.Any(char.IsControl))
            {
                claims.Add(new Claim("role", role));
            }
        }

        var identity = new ClaimsIdentity(claims, SchemeName, "sub", "role");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
