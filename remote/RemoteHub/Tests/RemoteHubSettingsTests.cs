using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using StayActive.RemoteHub.Configuration;

namespace StayActive.RemoteHub.Tests;

public sealed class RemoteHubSettingsTests
{
    [Fact]
    public void Production_without_oidc_configuration_fails_closed()
    {
        var builder = CreateBuilder(Environments.Production, new Dictionary<string, string?>
        {
            ["RemoteHub:Storage:JournalPath"] = Path.Combine(Path.GetTempPath(), "remotehub-settings", "inventory.journal.jsonl"),
            ["RemoteHub:Storage:JournalHmacKey"] = TestHmacKey()
        });

        var exception = Assert.Throws<InvalidOperationException>(() => RemoteHubSettings.Load(builder.Configuration, builder.Environment));

        Assert.Contains("Authority", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_rejects_the_explicit_local_development_switch()
    {
        var builder = CreateBuilder(Environments.Production, new Dictionary<string, string?>
        {
            ["RemoteHub:LocalDevelopment:Enabled"] = "true",
            ["RemoteHub:Storage:JournalPath"] = Path.Combine(Path.GetTempPath(), "remotehub-settings", "inventory.journal.jsonl"),
            ["RemoteHub:Storage:JournalHmacKey"] = TestHmacKey()
        });

        var exception = Assert.Throws<InvalidOperationException>(() => RemoteHubSettings.Load(builder.Configuration, builder.Environment));

        Assert.Contains("forbidden in Production", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Development_mode_still_requires_explicit_opt_in_and_integrity_key()
    {
        var builder = CreateBuilder(Environments.Development, new Dictionary<string, string?>
        {
            ["RemoteHub:LocalDevelopment:Enabled"] = "true",
            ["RemoteHub:Storage:JournalPath"] = Path.Combine(Path.GetTempPath(), "remotehub-settings", "inventory.journal.jsonl"),
            ["RemoteHub:Storage:JournalHmacKey"] = TestHmacKey()
        });

        var settings = RemoteHubSettings.Load(builder.Configuration, builder.Environment);

        Assert.True(settings.LocalDevelopmentEnabled);
        Assert.Null(settings.OidcAuthority);
        Assert.Equal("remotehub.fleet.read", settings.FleetReadScope);
        Assert.Equal("stayactive.remotehub.admin", settings.AdministratorRole);
        Assert.Equal("remotehub.admin", settings.AdministratorRoleScope);
    }

    [Fact]
    public void Invalid_administrator_role_is_rejected()
    {
        var builder = CreateBuilder(Environments.Development, new Dictionary<string, string?>
        {
            ["RemoteHub:LocalDevelopment:Enabled"] = "true",
            ["RemoteHub:Storage:JournalPath"] = Path.Combine(Path.GetTempPath(), "remotehub-settings", "inventory.journal.jsonl"),
            ["RemoteHub:Storage:JournalHmacKey"] = TestHmacKey(),
            ["RemoteHub:Authorization:AdministratorRole"] = "not a role"
        });

        var exception = Assert.Throws<InvalidOperationException>(() => RemoteHubSettings.Load(builder.Configuration, builder.Environment));

        Assert.Contains("AdministratorRole", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void All_zero_integrity_key_is_rejected()
    {
        var builder = CreateBuilder(Environments.Development, new Dictionary<string, string?>
        {
            ["RemoteHub:LocalDevelopment:Enabled"] = "true",
            ["RemoteHub:Storage:JournalPath"] = Path.Combine(Path.GetTempPath(), "remotehub-settings", "inventory.journal.jsonl"),
            ["RemoteHub:Storage:JournalHmacKey"] = Convert.ToBase64String(new byte[32])
        });

        var exception = Assert.Throws<InvalidOperationException>(() => RemoteHubSettings.Load(builder.Configuration, builder.Environment));

        Assert.Contains("all-zero", exception.Message, StringComparison.Ordinal);
    }

    private static WebApplicationBuilder CreateBuilder(string environmentName, IDictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName,
            ApplicationName = typeof(RemoteHubSettings).Assembly.FullName
        });
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }

    private static string TestHmacKey() => Convert.ToBase64String(Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray());
}
