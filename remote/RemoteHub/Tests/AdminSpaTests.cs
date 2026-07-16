using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StayActive.RemoteHub.Configuration;

namespace StayActive.RemoteHub.Tests;

public sealed class AdminSpaTests
{
    [Fact]
    public void Admin_spa_settings_require_a_public_oidc_client_and_exact_https_origin()
    {
        var builder = CreateBuilder(Environments.Production, CreateSettings(Path.Combine(Path.GetTempPath(), "remotehub-admin-settings", "inventory.journal.jsonl")));

        var settings = RemoteHubSettings.Load(builder.Configuration, builder.Environment);

        var admin = Assert.IsType<AdminSpaSettings>(settings.AdminSpa);
        Assert.Equal("stayactive-remotehub-admin", admin.ClientId);
        Assert.Equal("https://remotehub.example.test/admin/", admin.RedirectUri);
        Assert.Contains("openid", admin.Scopes);
        Assert.Contains("remotehub.inventory.write", admin.Scopes);
        Assert.DoesNotContain("offline_access", admin.Scopes);
    }

    [Fact]
    public void Admin_spa_cannot_be_combined_with_header_based_local_development_authentication()
    {
        var values = CreateSettings(Path.Combine(Path.GetTempPath(), "remotehub-admin-settings", "inventory.journal.jsonl"));
        values["RemoteHub:LocalDevelopment:Enabled"] = "true";
        var builder = CreateBuilder(Environments.Development, values);

        var exception = Assert.Throws<InvalidOperationException>(() => RemoteHubSettings.Load(builder.Configuration, builder.Environment));

        Assert.Contains("cannot be enabled with local development header authentication", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_admin_spa_serves_only_public_configuration_and_leaves_api_protected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stayactive-remotehub-admin-spa", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var app = BuildApp(directory);
        try
        {
            await app.StartAsync();
            var address = Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses);
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            using var index = await client.GetAsync("/admin/");
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            var indexHtml = await index.Content.ReadAsStringAsync();
            Assert.Contains("RemoteHub Admin", indexHtml, StringComparison.Ordinal);
            Assert.Equal("no-store, max-age=0", index.Headers.CacheControl!.ToString());
            Assert.Contains("frame-ancestors 'none'", index.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
            Assert.Contains("bluetooth=()", index.Headers.GetValues("Permissions-Policy").Single(), StringComparison.Ordinal);

            using var script = await client.GetAsync("/admin/admin.js");
            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            var scriptContent = await script.Content.ReadAsStringAsync();
            Assert.Contains("code_challenge_method: \"S256\"", scriptContent, StringComparison.Ordinal);
            Assert.DoesNotContain("client_secret", scriptContent, StringComparison.Ordinal);

            using var configurationResponse = await client.GetAsync("/admin/config.json");
            Assert.Equal(HttpStatusCode.OK, configurationResponse.StatusCode);
            using var configurationDocument = JsonDocument.Parse(await configurationResponse.Content.ReadAsStringAsync());
            Assert.Equal("stayactive-remotehub-admin", configurationDocument.RootElement.GetProperty("clientId").GetString());
            Assert.Equal("https://remotehub.example.test/admin/", configurationDocument.RootElement.GetProperty("redirectUri").GetString());
            Assert.False(configurationDocument.RootElement.TryGetProperty("journalHmacKey", out _));

            using var protectedApi = await client.GetAsync("/api/v1/admin/inventory");
            Assert.Equal(HttpStatusCode.Unauthorized, protectedApi.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Admin_spa_assets_are_not_exposed_without_the_explicit_enable_switch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stayactive-remotehub-admin-disabled", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(RemoteHubApplication).Assembly.FullName,
            ContentRootPath = FindProjectRoot(),
            WebRootPath = Path.Combine(FindProjectRoot(), "wwwroot")
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteHub:LocalDevelopment:Enabled"] = "true",
            ["RemoteHub:Storage:JournalPath"] = Path.Combine(directory, "inventory.journal.jsonl"),
            ["RemoteHub:Storage:JournalHmacKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = RemoteHubApplication.Build(builder);
        try
        {
            await app.StartAsync();
            var address = Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses);
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            using var response = await client.GetAsync("/admin/");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static WebApplication BuildApp(string journalDirectory)
    {
        var root = FindProjectRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(RemoteHubApplication).Assembly.FullName,
            ContentRootPath = root,
            WebRootPath = Path.Combine(root, "wwwroot")
        });
        builder.Configuration.AddInMemoryCollection(CreateSettings(Path.Combine(journalDirectory, "inventory.journal.jsonl")));
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        return RemoteHubApplication.Build(builder);
    }

    private static WebApplicationBuilder CreateBuilder(string environmentName, IDictionary<string, string?> values)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName,
            ApplicationName = typeof(RemoteHubSettings).Assembly.FullName
        });
        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }

    private static Dictionary<string, string?> CreateSettings(string journalPath) => new()
    {
        ["RemoteHub:Authentication:Authority"] = "https://id.example.test/realms/stayactive",
        ["RemoteHub:Authentication:Audience"] = "stayactive-remotehub",
        ["RemoteHub:Storage:JournalPath"] = journalPath,
        ["RemoteHub:Storage:JournalHmacKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["RemoteHub:AdminSpa:Enabled"] = "true",
        ["RemoteHub:AdminSpa:PublicOrigin"] = "https://remotehub.example.test",
        ["RemoteHub:AdminSpa:ClientId"] = "stayactive-remotehub-admin"
    };

    private static string FindProjectRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "RemoteHub.csproj")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the RemoteHub project root for static asset testing.");
    }
}
