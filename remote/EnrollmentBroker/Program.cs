using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;
using StayActive.EnrollmentBroker.Auth;
using StayActive.EnrollmentBroker.Configuration;
using StayActive.EnrollmentBroker.Domain;
using StayActive.EnrollmentBroker.Persistence;
using StayActive.EnrollmentBroker.Provisioning;
using StayActive.EnrollmentBroker.Security;
using StayActive.EnrollmentBroker.Services;

var credentialStore = new WindowsCredentialManagerStore();
if (ControllerCredentialProvisioner.TryHandle(
        args,
        Console.In,
        credentialStore,
        Console.Out,
        Console.Error,
        out var provisioningExitCode))
{
    Environment.ExitCode = provisioningExitCode;
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    ApplicationName = typeof(EnrollmentBrokerApplication).Assembly.FullName
});
var app = EnrollmentBrokerApplication.Build(builder, credentialStore);
await app.RunAsync();

/// <summary>
/// Composition root kept public so tests exercise the complete production
/// authentication, authorization, persistence, and endpoint pipeline.
/// </summary>
public static class EnrollmentBrokerApplication
{
    public static WebApplication Build(
        WebApplicationBuilder builder,
        IControllerCredentialStore? credentialStore = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        credentialStore ??= new WindowsCredentialManagerStore();
        builder.Host.UseWindowsService();

        var settings = EnrollmentBrokerSettings.Load(builder.Configuration, builder.Environment);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 8 * 1024);
        if (settings.LocalDevelopmentEnabled && builder.Environment.IsDevelopment())
        {
            // Header auth is permitted only on an explicit loopback listener.
            builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(settings.LocalDevelopmentPort));
        }

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            var json = EnrollmentBrokerJson.CreateOptions();
            options.SerializerOptions.PropertyNamingPolicy = json.PropertyNamingPolicy;
            options.SerializerOptions.PropertyNameCaseInsensitive = json.PropertyNameCaseInsensitive;
            options.SerializerOptions.WriteIndented = json.WriteIndented;
            foreach (var converter in json.Converters)
            {
                options.SerializerOptions.Converters.Add(converter);
            }
        });

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<IControllerCredentialStore>(credentialStore);
        // Construct eagerly: a tampered journal must prevent the broker from
        // serving enrollment, rather than failing after a sensitive key exists.
        builder.Services.TryAddSingleton<IEnrollmentTicketStore>(_ =>
            new FileEnrollmentTicketStore(settings.JournalPath, settings.CreateJournalHmacKeyCopy()));
        builder.Services.TryAddSingleton<TimeProvider>(_ => TimeProvider.System);
        builder.Services.TryAddSingleton<IHeadscaleV029Client>(serviceProvider =>
        {
            var client = new HttpClient
            {
                BaseAddress = settings.HeadscaleApiBaseUri,
                Timeout = TimeSpan.FromSeconds(15)
            };
            return new HeadscaleV029Client(
                client,
                serviceProvider.GetRequiredService<IControllerCredentialStore>());
        });
        builder.Services.AddSingleton<EnrollmentTicketService>();

        ConfigureAuthentication(builder.Services, settings);
        builder.Services.AddAuthorization(options => EnrollmentBrokerAuthorization.AddPolicies(options, settings));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // The global limiter deliberately applies only to the sensitive
            // creation route. It does not trust X-Forwarded-For or any other
            // caller-controlled network header as a partition key.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                IsTicketCreationRequest(context)
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        "enrollment-ticket-create-global",
                        static _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 20,
                            Window = TimeSpan.FromMinutes(1),
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            QueueLimit = 0,
                            AutoReplenishment = true
                        })
                    : RateLimitPartition.GetNoLimiter("non-ticket-creation"));
            options.AddPolicy(EnrollmentBrokerSettings.TicketCreationRateLimitPolicy, context =>
            {
                var subject = EnrollmentBrokerIdentity.GetActorSubject(context.User) ?? "unauthenticated";
                return RateLimitPartition.GetFixedWindowLimiter(
                    $"enrollment-ticket-create-subject:{subject}",
                    static _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3,
                        Window = TimeSpan.FromMinutes(5),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });
        });

        var app = builder.Build();
        _ = app.Services.GetRequiredService<IHeadscaleV029Client>();
        app.Use(async (context, next) =>
        {
            // The POST response contains the raw one-time key.  Apply these to
            // every response, because status metadata should not be cached or
            // leaked through a referrer either.
            context.Response.Headers.CacheControl = "no-store, max-age=0";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next(context).ConfigureAwait(false);
        });
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

        app.MapPost("/api/v1/enrollment-tickets", async (
            EnrollmentTicketCreateRequest request,
            HttpContext context,
            EnrollmentTicketService tickets,
            CancellationToken cancellationToken) =>
        {
            var actorSubject = EnrollmentBrokerIdentity.GetActorSubject(context.User);
            if (actorSubject is null)
            {
                return Results.Forbid();
            }

            try
            {
                var result = await tickets.IssueAsync(request, actorSubject, context.TraceIdentifier, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Created($"/api/v1/enrollment-tickets/{result.Ticket.Id:D}", result);
            }
            catch (EnrollmentTicketValidationException exception)
            {
                return Results.ValidationProblem(exception.Errors);
            }
            catch (HeadscaleApiException)
            {
                return UpstreamUnavailable();
            }
            catch (HeadscaleUnavailableException)
            {
                return UpstreamUnavailable();
            }
            catch (HeadscaleProtocolException)
            {
                return UpstreamUnavailable();
            }
            catch (EnrollmentTicketUnsafeHeadscaleResponseException)
            {
                return UpstreamUnavailable();
            }
            catch (EnrollmentTicketUnsafeStateException)
            {
                return ServiceUnavailable();
            }
        }).RequireAuthorization(EnrollmentBrokerSettings.EnrollmentWritePolicy)
            .RequireRateLimiting(EnrollmentBrokerSettings.TicketCreationRateLimitPolicy);

        app.MapGet("/api/v1/enrollment-tickets/{ticketId:guid}", async (
            Guid ticketId,
            HttpContext context,
            EnrollmentTicketService tickets,
            CancellationToken cancellationToken) =>
        {
            var actorSubject = EnrollmentBrokerIdentity.GetActorSubject(context.User);
            if (actorSubject is null)
            {
                return Results.Forbid();
            }

            try
            {
                var ticket = await tickets.GetForOwnerAsync(ticketId, actorSubject, context.TraceIdentifier, cancellationToken)
                    .ConfigureAwait(false);
                return ticket is null
                    ? Results.NotFound()
                    : Results.Ok(new EnrollmentTicketResponse(tickets.ToView(ticket)));
            }
            catch (HeadscaleApiException)
            {
                return UpstreamUnavailable();
            }
            catch (HeadscaleUnavailableException)
            {
                return UpstreamUnavailable();
            }
            catch (HeadscaleProtocolException)
            {
                return UpstreamUnavailable();
            }
            catch (EnrollmentTicketUnsafeHeadscaleResponseException)
            {
                return UpstreamUnavailable();
            }
            catch (EnrollmentTicketUnsafeStateException)
            {
                return ServiceUnavailable();
            }
        }).RequireAuthorization(EnrollmentBrokerSettings.EnrollmentWritePolicy);

        app.MapDelete("/api/v1/enrollment-tickets/{ticketId:guid}", async (
            Guid ticketId,
            HttpContext context,
            EnrollmentTicketService tickets,
            CancellationToken cancellationToken) =>
        {
            var actorSubject = EnrollmentBrokerIdentity.GetActorSubject(context.User);
            if (actorSubject is null)
            {
                return Results.Forbid();
            }

            try
            {
                var result = await tickets.RevokeForOwnerAsync(ticketId, actorSubject, context.TraceIdentifier, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Found)
                {
                    return Results.NotFound();
                }

                if (result.AlreadyRedeemed)
                {
                    return Results.Conflict(new
                    {
                        error = "ticket_already_redeemed",
                        ticket = tickets.ToView(result.Ticket!)
                    });
                }

                return Results.Ok(new EnrollmentTicketResponse(tickets.ToView(result.Ticket!)));
            }
            catch (HeadscaleApiException)
            {
                return UpstreamUnavailable();
            }
            catch (HeadscaleUnavailableException)
            {
                return UpstreamUnavailable();
            }
            catch (HeadscaleProtocolException)
            {
                return UpstreamUnavailable();
            }
            catch (EnrollmentTicketUnsafeHeadscaleResponseException)
            {
                return UpstreamUnavailable();
            }
            catch (EnrollmentTicketUnsafeStateException)
            {
                return ServiceUnavailable();
            }
        }).RequireAuthorization(EnrollmentBrokerSettings.EnrollmentWritePolicy);

        return app;
    }

    private static IResult UpstreamUnavailable() => Results.Problem(
        title: "Enrollment upstream unavailable",
        statusCode: StatusCodes.Status502BadGateway);

    private static IResult ServiceUnavailable() => Results.Problem(
        title: "Enrollment service unavailable",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private static bool IsTicketCreationRequest(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method)
        && string.Equals(
            context.Request.Path,
            "/api/v1/enrollment-tickets",
            StringComparison.Ordinal);

    private static void ConfigureAuthentication(IServiceCollection services, EnrollmentBrokerSettings settings)
    {
        if (settings.LocalDevelopmentEnabled)
        {
            services
                .AddAuthentication(DevelopmentHeaderAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentHeaderAuthenticationHandler>(
                    DevelopmentHeaderAuthenticationHandler.SchemeName,
                    static _ => { });
            return;
        }

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = settings.OidcAuthority;
                options.Audience = settings.OidcAudience;
                options.RequireHttpsMetadata = true;
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = settings.OidcAuthority,
                    ValidateAudience = true,
                    ValidAudience = settings.OidcAudience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(2),
                    NameClaimType = "name",
                    RoleClaimType = "role"
                };
            });
    }
}

public partial class Program;
