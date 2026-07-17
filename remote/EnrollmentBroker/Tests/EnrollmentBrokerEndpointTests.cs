using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using StayActive.EnrollmentBroker.Auth;
using StayActive.EnrollmentBroker.Configuration;
using StayActive.EnrollmentBroker.Domain;
using StayActive.EnrollmentBroker.Services;

namespace StayActive.EnrollmentBroker.Tests;

public sealed class EnrollmentBrokerEndpointTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "stayactive-enrollmentbroker-endpoints", Guid.NewGuid().ToString("N"));
    private readonly byte[] _journalKey = RandomNumberGenerator.GetBytes(32);
    private readonly FakeHeadscaleClient _headscale = new();
    private byte[]? _jwtSigningBytes;
    private WebApplication? _app;

    [Fact]
    public async Task Ticket_endpoints_issue_a_key_once_and_never_return_or_journal_it_again()
    {
        var client = await StartClientAsync();
        try
        {
            using var create = CreateRequest(HttpMethod.Post, "/api/v1/enrollment-tickets");
            create.Content = new StringContent(
                "{\"kind\":\"exitNode\",\"lifetimeMinutes\":15}",
                Encoding.UTF8,
                "application/json");
            using var createResponse = await client.SendAsync(create);

            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            Assert.Equal("no-store, max-age=0", createResponse.Headers.CacheControl!.ToString());
            var createText = await createResponse.Content.ReadAsStringAsync();
            using var createDocument = JsonDocument.Parse(createText);
            var authKey = createDocument.RootElement.GetProperty("authKey").GetString();
            Assert.Equal(FakeHeadscaleClient.RawKey, authKey);
            var ticket = createDocument.RootElement.GetProperty("ticket");
            var ticketId = ticket.GetProperty("id").GetGuid();
            Assert.Equal("exitNode", ticket.GetProperty("kind").GetString());
            Assert.Equal("issued", ticket.GetProperty("status").GetString());
            Assert.Equal("https://headscale.stayactive.test", ticket.GetProperty("loginServer").GetString());
            Assert.Equal(
                ["tag:stayactive", "tag:stayactive-exit"],
                ticket.GetProperty("advertiseTags").EnumerateArray().Select(element => element.GetString()!).ToArray());
            Assert.DoesNotContain("--auth-key", createText, StringComparison.Ordinal);

            using var get = CreateRequest(HttpMethod.Get, $"/api/v1/enrollment-tickets/{ticketId:D}");
            using var getResponse = await client.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
            var getText = await getResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain(FakeHeadscaleClient.RawKey, getText, StringComparison.Ordinal);
            Assert.DoesNotContain("authKey", getText, StringComparison.OrdinalIgnoreCase);

            using var revoke = CreateRequest(HttpMethod.Delete, $"/api/v1/enrollment-tickets/{ticketId:D}");
            using var revokeResponse = await client.SendAsync(revoke);
            Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);
            using var revokeDocument = JsonDocument.Parse(await revokeResponse.Content.ReadAsStringAsync());
            Assert.Equal("revoked", revokeDocument.RootElement.GetProperty("ticket").GetProperty("status").GetString());
            Assert.Equal(["42"], _headscale.ExpiredKeyIds);

            var journal = await File.ReadAllTextAsync(Path.Combine(_directory, "tickets.journal.jsonl"));
            Assert.DoesNotContain(FakeHeadscaleClient.RawKey, journal, StringComparison.Ordinal);
            Assert.DoesNotContain("authKey", journal, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            client.Dispose();
            await StopAndCleanUpAsync();
        }
    }

    [Fact]
    public async Task Ticket_creation_fails_closed_and_revokes_if_headscale_extends_the_requested_lifetime()
    {
        _headscale.CreatedExpirationOffset = TimeSpan.FromSeconds(1);
        var client = await StartClientAsync();
        try
        {
            using var request = CreateRequest(HttpMethod.Post, "/api/v1/enrollment-tickets");
            request.Content = new StringContent("{\"kind\":\"device\",\"lifetimeMinutes\":15}", Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.Equal(["42"], _headscale.ExpiredKeyIds);
            var responseText = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(FakeHeadscaleClient.RawKey, responseText, StringComparison.Ordinal);
        }
        finally
        {
            client.Dispose();
            await StopAndCleanUpAsync();
        }
    }
    [Fact]
    public async Task Ticket_endpoints_require_development_scope_and_role_and_hide_other_owners_tickets()
    {
        var client = await StartClientAsync();
        try
        {
            using (var unauthenticated = await client.PostAsync(
                       "/api/v1/enrollment-tickets",
                       new StringContent("{\"kind\":\"device\",\"lifetimeMinutes\":15}", Encoding.UTF8, "application/json")))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            }

            using (var missingRole = CreateRequest(HttpMethod.Post, "/api/v1/enrollment-tickets", roles: string.Empty))
            {
                missingRole.Content = new StringContent("{\"kind\":\"device\",\"lifetimeMinutes\":15}", Encoding.UTF8, "application/json");
                using var missingRoleResponse = await client.SendAsync(missingRole);
                Assert.Equal(HttpStatusCode.Forbidden, missingRoleResponse.StatusCode);
            }

            using var create = CreateRequest(HttpMethod.Post, "/api/v1/enrollment-tickets", subject: "owner-a");
            create.Content = new StringContent("{\"kind\":\"device\",\"lifetimeMinutes\":15}", Encoding.UTF8, "application/json");
            using var createResponse = await client.SendAsync(create);
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
            using var document = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
            var ticketId = document.RootElement.GetProperty("ticket").GetProperty("id").GetGuid();

            using var otherOwner = CreateRequest(HttpMethod.Get, $"/api/v1/enrollment-tickets/{ticketId:D}", subject: "owner-b");
            using var otherOwnerResponse = await client.SendAsync(otherOwner);
            Assert.Equal(HttpStatusCode.NotFound, otherOwnerResponse.StatusCode);
        }
        finally
        {
            client.Dispose();
            await StopAndCleanUpAsync();
        }
    }

    [Fact]
    public async Task Validation_rejects_unmapped_role_or_tag_fields_and_never_calls_headscale()
    {
        var client = await StartClientAsync();
        try
        {
            using var request = CreateRequest(HttpMethod.Post, "/api/v1/enrollment-tickets");
            request.Content = new StringContent(
                "{\"kind\":\"device\",\"lifetimeMinutes\":15,\"tags\":[\"tag:admin\"]}",
                Encoding.UTF8,
                "application/json");
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, _headscale.CreateCalls);
        }
        finally
        {
            client.Dispose();
            await StopAndCleanUpAsync();
        }
    }

    [Fact]
    public async Task Ticket_creation_uses_fixed_global_and_authenticated_subject_rate_limits_not_forwarded_for()
    {
        var client = await StartClientAsync();
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                using var request = CreateRequest(HttpMethod.Post, "/api/v1/enrollment-tickets", subject: "rate-limited-owner");
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", $"198.51.100.{attempt + 10}");
                request.Content = new StringContent("{\"kind\":\"device\",\"lifetimeMinutes\":15}", Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);
                if (attempt < 3)
                {
                    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                }
                else
                {
                    Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
                }
            }

            Assert.Equal(3, _headscale.CreateCalls);
        }
        finally
        {
            client.Dispose();
            await StopAndCleanUpAsync();
        }
    }
    [Fact]
    public async Task Ticket_creation_has_a_conservative_global_cap_even_for_distinct_authenticated_subjects()
    {
        var client = await StartClientAsync();
        try
        {
            for (var attempt = 0; attempt < 21; attempt++)
            {
                using var request = CreateRequest(
                    HttpMethod.Post,
                    "/api/v1/enrollment-tickets",
                    subject: $"global-rate-owner-{attempt}");
                request.Content = new StringContent("{\"kind\":\"device\",\"lifetimeMinutes\":15}", Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request);
                Assert.Equal(
                    attempt < 20 ? HttpStatusCode.Created : HttpStatusCode.TooManyRequests,
                    response.StatusCode);
            }

            Assert.Equal(20, _headscale.CreateCalls);
        }
        finally
        {
            client.Dispose();
            await StopAndCleanUpAsync();
        }
    }
    [Fact]
    public async Task Revocation_reports_redeemed_ticket_instead_of_claiming_it_was_revoked()
    {
        var client = await StartClientAsync();
        try
        {
            using var create = CreateRequest(HttpMethod.Post, "/api/v1/enrollment-tickets");
            create.Content = new StringContent("{\"kind\":\"device\",\"lifetimeMinutes\":15}", Encoding.UTF8, "application/json");
            using var createResponse = await client.SendAsync(create);
            using var document = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
            var ticketId = document.RootElement.GetProperty("ticket").GetProperty("id").GetGuid();
            _headscale.MarkUsed("42");

            using var revoke = CreateRequest(HttpMethod.Delete, $"/api/v1/enrollment-tickets/{ticketId:D}");
            using var response = await client.SendAsync(revoke);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var responseDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("ticket_already_redeemed", responseDocument.RootElement.GetProperty("error").GetString());
            Assert.Equal("redeemed", responseDocument.RootElement.GetProperty("ticket").GetProperty("status").GetString());
            Assert.Empty(_headscale.ExpiredKeyIds);
        }
        finally
        {
            client.Dispose();
            await StopAndCleanUpAsync();
        }
    }

    [Fact]
    public async Task Production_uses_bearer_jwt_not_development_headers()
    {
        var client = await StartClientAsync(production: true);
        try
        {
            var schemeProvider = _app!.Services.GetRequiredService<IAuthenticationSchemeProvider>();
            var defaultScheme = await schemeProvider.GetDefaultAuthenticateSchemeAsync();
            Assert.Equal(JwtBearerDefaults.AuthenticationScheme, defaultScheme!.Name);

            using var request = CreateRequest(HttpMethod.Get, "/api/v1/enrollment-tickets/11111111-1111-1111-1111-111111111111");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            client.Dispose();
            await StopAndCleanUpAsync();
        }
    }

    [Fact]
    public async Task Production_validates_a_signed_issuer_audience_scope_and_role_jwt()
    {
        var client = await StartClientAsync(production: true, useStaticJwtConfiguration: true);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/enrollment-tickets")
            {
                Content = new StringContent("{\"kind\":\"device\",\"lifetimeMinutes\":15}", Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CreateProductionJwt());
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        finally
        {
            client.Dispose();
            await StopAndCleanUpAsync();
        }
    }

    [Fact]
    public void Sensitive_response_and_upstream_key_types_redact_their_ToString_output()
    {
        const string rawKey = "hskey-auth-this-must-never-appear-in-diagnostics";
        var ticket = new EnrollmentTicketView(
            Guid.NewGuid(),
            "device",
            "issued",
            DateTimeOffset.UtcNow.AddMinutes(15),
            "https://headscale.stayactive.test",
            ["tag:stayactive"]);
        var response = new EnrollmentTicketCreateResponse(ticket, rawKey);
        var upstream = new HeadscaleCreatedPreAuthKey(
            "42",
            rawKey,
            reusable: false,
            ephemeral: false,
            used: false,
            DateTimeOffset.UtcNow.AddMinutes(15),
            ["tag:stayactive"]);

        Assert.DoesNotContain(rawKey, response.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(rawKey, upstream.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", response.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", upstream.ToString(), StringComparison.Ordinal);
    }

    private async Task<HttpClient> StartClientAsync(bool production = false, bool useStaticJwtConfiguration = false)
    {
        Directory.CreateDirectory(_directory);
        var journalKeyFile = Path.Combine(_directory, "journal-hmac-key");
        await File.WriteAllTextAsync(journalKeyFile, Convert.ToBase64String(_journalKey));

        var environment = production ? "Production" : "Testing";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ApplicationName = typeof(EnrollmentBrokerApplication).Assembly.FullName
        });
        var values = new Dictionary<string, string?>
        {
            ["EnrollmentBroker:Storage:JournalPath"] = Path.Combine(_directory, "tickets.journal.jsonl"),
            ["EnrollmentBroker:Storage:JournalHmacKeyFile"] = journalKeyFile,
            ["EnrollmentBroker:Headscale:ApiBaseUrl"] = EnrollmentBrokerSettings.HeadscaleControllerApiBaseUrl,
            ["EnrollmentBroker:Headscale:UserId"] = "1",
            ["EnrollmentBroker:Headscale:LoginServer"] = "https://headscale.stayactive.test"
        };
        if (production)
        {
            values["EnrollmentBroker:Authentication:Authority"] = "https://keycloak.stayactive.test/realms/stayactive";
            values["EnrollmentBroker:Authentication:Audience"] = "stayactive-enrollment";
        }
        else
        {
            values["EnrollmentBroker:LocalDevelopment:Enabled"] = "true";
        }

        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddSingleton<IHeadscaleV029Client>(_headscale);
        if (useStaticJwtConfiguration)
        {
            Assert.True(production);
            _jwtSigningBytes = RandomNumberGenerator.GetBytes(32);
            var signingKey = new SymmetricSecurityKey(_jwtSigningBytes);
            var oidcConfiguration = new OpenIdConnectConfiguration
            {
                Issuer = "https://keycloak.stayactive.test/realms/stayactive"
            };
            oidcConfiguration.SigningKeys.Add(signingKey);
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.ConfigurationManager = new StaticConfigurationManager(oidcConfiguration);
                options.RefreshOnIssuerKeyNotFound = false;
                options.TokenValidationParameters.IssuerSigningKey = signingKey;
            });
        }
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = EnrollmentBrokerApplication.Build(builder, new FakeControllerCredentialStore());
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses;
        return new HttpClient { BaseAddress = new Uri(Assert.Single(addresses)) };
    }

    private string CreateProductionJwt()
    {
        var signingKey = new SymmetricSecurityKey(_jwtSigningBytes ?? throw new InvalidOperationException("JWT test signing key is unavailable."));
        var token = new JwtSecurityToken(
            issuer: "https://keycloak.stayactive.test/realms/stayactive",
            audience: "stayactive-enrollment",
            claims:
            [
                new Claim("sub", "jwt-operator"),
                new Claim("scope", "stayactive.enrollment.write"),
                new Claim("role", "stayactive.enrollment.admin")
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        string subject = "test-operator",
        string scopes = "stayactive.enrollment.write",
        string roles = "stayactive.enrollment.admin")
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(DevelopmentHeaderAuthenticationHandler.SubjectHeader, subject);
        request.Headers.Add(DevelopmentHeaderAuthenticationHandler.ScopesHeader, scopes);
        if (!string.IsNullOrEmpty(roles))
        {
            request.Headers.Add(DevelopmentHeaderAuthenticationHandler.RolesHeader, roles);
        }

        return request;
    }

    private async Task StopAndCleanUpAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }

        CryptographicOperations.ZeroMemory(_journalKey);
        if (_jwtSigningBytes is not null)
        {
            CryptographicOperations.ZeroMemory(_jwtSigningBytes);
            _jwtSigningBytes = null;
        }
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class StaticConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private readonly OpenIdConnectConfiguration _configuration;

        public StaticConfigurationManager(OpenIdConnectConfiguration configuration)
        {
            _configuration = configuration;
        }

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            Task.FromResult(_configuration);

        public void RequestRefresh()
        {
        }
    }

    private sealed class FakeHeadscaleClient : IHeadscaleV029Client
    {
        private readonly Dictionary<string, HeadscalePreAuthKeyStatus> _keys = new(StringComparer.Ordinal);
        private long _nextKeyId = 41;

        public const string RawKey = "hskey-auth-unit-test-raw-secret";

        public int CreateCalls { get; private set; }

        public TimeSpan CreatedExpirationOffset { get; set; }

        public List<string> ExpiredKeyIds { get; } = [];

        public Task<HeadscaleCreatedPreAuthKey> CreateOneUsePreAuthKeyAsync(
            string userId,
            DateTimeOffset expirationUtc,
            IReadOnlyList<string> aclTags,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            var keyId = Interlocked.Increment(ref _nextKeyId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var status = new HeadscalePreAuthKeyStatus(keyId, false, false, false, expirationUtc + CreatedExpirationOffset, aclTags.ToArray());
            _keys[status.Id] = status;
            return Task.FromResult(new HeadscaleCreatedPreAuthKey(
                status.Id,
                RawKey,
                status.Reusable,
                status.Ephemeral,
                status.Used,
                status.ExpirationUtc,
                status.AclTags.ToArray()));
        }

        public Task<HeadscalePreAuthKeyStatus?> GetPreAuthKeyStatusAsync(string keyId, CancellationToken cancellationToken) =>
            Task.FromResult(_keys.TryGetValue(keyId, out var status) ? status : null);

        public Task ExpirePreAuthKeyAsync(string keyId, CancellationToken cancellationToken)
        {
            ExpiredKeyIds.Add(keyId);
            return Task.CompletedTask;
        }

        public void MarkUsed(string keyId)
        {
            var current = _keys[keyId];
            _keys[keyId] = current with { Used = true };
        }
    }
}
