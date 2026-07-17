using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace StayActive.EnrollmentBroker.Auth;

/// <summary>
/// Test/development-only header authentication.  It is never registered in
/// Production, and Development binds the broker to loopback before this
/// handler can be reached.  It deliberately accepts no passwords or keys.
/// </summary>
public sealed class DevelopmentHeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "EnrollmentBrokerLocalDevelopment";
    public const string SubjectHeader = "X-EnrollmentBroker-Dev-Subject";
    public const string ScopesHeader = "X-EnrollmentBroker-Dev-Scopes";
    public const string RolesHeader = "X-EnrollmentBroker-Dev-Roles";

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
        if (!_environment.IsEnvironment("Testing"))
        {
            var remoteAddress = Context.Connection.RemoteIpAddress;
            if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    "Local development authentication only accepts loopback requests."));
            }
        }

        var subject = Request.Headers[SubjectHeader].ToString().Trim();
        var scopes = Request.Headers[ScopesHeader].ToString().Trim();
        var roles = Request.Headers[RolesHeader].ToString().Trim();
        if (subject.Length == 0 || scopes.Length == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!EnrollmentBrokerIdentity.IsSafeSubject(subject))
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
