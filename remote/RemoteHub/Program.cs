using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using StayActive.RemoteHub.Auth;
using StayActive.RemoteHub.Configuration;
using StayActive.RemoteHub.Domain;
using StayActive.RemoteHub.Persistence;

var builder = WebApplication.CreateBuilder(args);
var app = RemoteHubApplication.Build(builder);
await app.RunAsync();

/// <summary>
/// Composition root kept public so endpoint tests can run the exact production
/// pipeline on an ephemeral loopback listener.
/// </summary>
public static class RemoteHubApplication
{
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var settings = RemoteHubSettings.Load(builder.Configuration, builder.Environment);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024);
        if (settings.LocalDevelopmentEnabled && builder.Environment.IsDevelopment())
        {
            // Development header auth can only reach this loopback listener.
            builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(settings.LocalDevelopmentPort));
        }

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            var json = RemoteHubJson.CreateOptions();
            options.SerializerOptions.PropertyNamingPolicy = json.PropertyNamingPolicy;
            options.SerializerOptions.WriteIndented = json.WriteIndented;
            foreach (var converter in json.Converters)
            {
                options.SerializerOptions.Converters.Add(converter);
            }
        });
        builder.Services.AddSingleton(settings);
        // Construct eagerly so a tampered/corrupt audit journal prevents the
        // process from starting rather than failing only on its first request.
        var inventoryStore = new FileRemoteInventoryStore(settings.JournalPath, settings.JournalHmacKey);
        builder.Services.AddSingleton<IRemoteInventoryStore>(inventoryStore);

        ConfigureAuthentication(builder.Services, settings);
        builder.Services.AddAuthorization(options => RemoteHubAuthorization.AddPolicies(options, settings));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        ConfigureAdminSpa(app, settings);

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
            .AllowAnonymous();

        app.MapGet("/api/v1/fleet", async (IRemoteInventoryStore store, CancellationToken cancellationToken) =>
            Results.Ok(new FleetResponse(await store.ListAsync(verifiedOnly: true, cancellationToken))))
            .RequireAuthorization(RemoteHubSettings.FleetReadPolicy);

        app.MapGet("/api/v1/admin/inventory", async (IRemoteInventoryStore store, CancellationToken cancellationToken) =>
            Results.Ok(new FleetResponse(await store.ListAsync(verifiedOnly: false, cancellationToken))))
            .RequireAuthorization(RemoteHubSettings.InventoryWritePolicy);

        app.MapPut("/api/v1/admin/inventory/{headscaleNodeId}", async (
            string headscaleNodeId,
            InventoryUpdateRequest request,
            HttpContext context,
            IRemoteInventoryStore store,
            CancellationToken cancellationToken) =>
        {
            if (!RemoteInventoryValidation.TryNormalizeNodeId(headscaleNodeId, out var normalizedNodeId, out var nodeError))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["headscaleNodeId"] = [nodeError!]
                });
            }

            if (!RemoteInventoryValidation.TryNormalizeUpdate(request, out var update, out var errors))
            {
                return Results.ValidationProblem(errors);
            }

            var actorSubject = RemoteHubIdentity.GetActorSubject(context.User);
            if (actorSubject is null)
            {
                // The authorization policy normally prevents this path. Keep the
                // check to preserve an attributable audit trail if it changes.
                return Results.Forbid();
            }

            var result = await store.UpsertAsync(
                normalizedNodeId,
                update!,
                actorSubject,
                context.TraceIdentifier,
                cancellationToken);
            if (!result.Succeeded)
            {
                return Results.Conflict(new
                {
                    error = "inventory_version_conflict",
                    currentVersion = result.CurrentVersion
                });
            }

            return result.Created
                ? Results.Created($"/api/v1/admin/inventory/{Uri.EscapeDataString(normalizedNodeId)}", result.Record)
                : Results.Ok(result.Record);
        }).RequireAuthorization(RemoteHubSettings.InventoryWritePolicy);

        app.MapGet("/api/v1/admin/audit", async (
            int? take,
            IRemoteInventoryStore store,
            CancellationToken cancellationToken) =>
        {
            var requestedTake = take ?? 100;
            if (requestedTake is < 1 or > 500)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["take"] = ["Take must be between 1 and 500."]
                });
            }

            return Results.Ok(new AuditResponse(await store.ReadAuditAsync(requestedTake, cancellationToken)));
        }).RequireAuthorization(RemoteHubSettings.AuditReadPolicy);

        return app;
    }

    private static void ConfigureAdminSpa(WebApplication app, RemoteHubSettings settings)
    {
        if (settings.AdminSpa is not { } adminSpa)
        {
            return;
        }

        var webRoot = app.Environment.WebRootPath
            ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        var adminDirectory = Path.Combine(webRoot, "admin");
        if (!Directory.Exists(adminDirectory))
        {
            throw new InvalidOperationException(
                "RemoteHub Admin SPA is enabled but its published wwwroot/admin assets are unavailable.");
        }

        var provider = new PhysicalFileProvider(adminDirectory);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
            RequestPath = "/admin",
            OnPrepareResponse = context => ApplyAdminSpaSecurityHeaders(context.Context.Response.Headers, adminSpa)
        });

        app.MapGet("/admin/", (HttpContext context) =>
        {
            ApplyAdminSpaSecurityHeaders(context.Response.Headers, adminSpa);
            return Results.File(Path.Combine(adminDirectory, "index.html"), "text/html; charset=utf-8");
        }).AllowAnonymous();
        app.MapGet("/admin/config.json", (HttpContext context) =>
        {
            ApplyAdminSpaSecurityHeaders(context.Response.Headers, adminSpa);
            return Results.Ok(adminSpa.ToPublicConfiguration());
        }).AllowAnonymous();
    }

    private static void ApplyAdminSpaSecurityHeaders(IHeaderDictionary headers, AdminSpaSettings adminSpa)
    {
        var issuerOrigin = new Uri(adminSpa.Authority).GetLeftPart(UriPartial.Authority);
        headers.CacheControl = "no-store, max-age=0";
        headers.Pragma = "no-cache";
        headers["Content-Security-Policy"] =
            $"default-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'; object-src 'none'; "
            + $"script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self' {issuerOrigin}";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=(), bluetooth=(), usb=(), payment=()";
    }

    private static void ConfigureAuthentication(IServiceCollection services, RemoteHubSettings settings)
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

    private sealed record FleetResponse(IReadOnlyList<RemoteInventoryRecord> Devices);

    private sealed record AuditResponse(IReadOnlyList<RemoteHubAuditEvent> Events);
}

public partial class Program;
