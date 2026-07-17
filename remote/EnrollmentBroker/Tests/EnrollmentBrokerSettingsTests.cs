using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using StayActive.EnrollmentBroker.Configuration;

namespace StayActive.EnrollmentBroker.Tests;

public sealed class EnrollmentBrokerSettingsTests
{
    [Fact]
    public void Production_settings_accept_a_Keycloak_realm_issuer_and_redact_all_configured_secrets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stayactive-enrollmentbroker-settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var journalKeyFile = Path.Combine(directory, "journal-hmac-key");
        var journalKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            File.WriteAllText(journalKeyFile, Convert.ToBase64String(journalKey));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EnrollmentBroker:Authentication:Authority"] = "https://keycloak.stayactive.test/realms/stayactive",
                    ["EnrollmentBroker:Authentication:Audience"] = "stayactive-enrollment",
                    ["EnrollmentBroker:Storage:JournalPath"] = Path.Combine(directory, "tickets.journal.jsonl"),
                    ["EnrollmentBroker:Storage:JournalHmacKeyFile"] = journalKeyFile,
                    ["EnrollmentBroker:Headscale:ApiBaseUrl"] = EnrollmentBrokerSettings.HeadscaleControllerApiBaseUrl,
                    ["EnrollmentBroker:Headscale:UserId"] = "1",
                    ["EnrollmentBroker:Headscale:LoginServer"] = "https://headscale.stayactive.test"
                })
                .Build();

            var settings = EnrollmentBrokerSettings.Load(configuration, new TestWebHostEnvironment("Production", directory));

            Assert.Equal("https://keycloak.stayactive.test/realms/stayactive", settings.OidcAuthority);
            Assert.Equal("stayactive-enrollment", settings.OidcAudience);
            Assert.Equal("stayactive.enrollment.write", settings.EnrollmentWriteScope);
            var diagnostic = settings.ToString();
            Assert.DoesNotContain(Convert.ToBase64String(journalKey), diagnostic, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(journalKey);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Settings_reject_legacy_controller_key_configuration_without_echoing_it()
    {
        const string rawControllerKey = "hskey-api-settings-test-secret";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnrollmentBroker:LocalDevelopment:Enabled"] = "true",
                ["EnrollmentBroker:Storage:JournalPath"] = Path.Combine(Path.GetTempPath(), "tickets.journal.jsonl"),
                ["EnrollmentBroker:Storage:JournalHmacKey"] = Convert.ToBase64String(new byte[32]),
                ["EnrollmentBroker:Headscale:ApiBaseUrl"] = EnrollmentBrokerSettings.HeadscaleControllerApiBaseUrl,
                ["EnrollmentBroker:Headscale:ApiKey"] = rawControllerKey,
                ["EnrollmentBroker:Headscale:UserId"] = "1",
                ["EnrollmentBroker:Headscale:LoginServer"] = "https://headscale.stayactive.test"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            EnrollmentBrokerSettings.Load(configuration, new TestWebHostEnvironment("Testing", Path.GetTempPath())));

        Assert.Contains("ApiKey", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(rawControllerKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_rejects_any_headscale_api_origin_except_the_fixed_controller()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnrollmentBroker:LocalDevelopment:Enabled"] = "true",
                ["EnrollmentBroker:Storage:JournalPath"] = Path.Combine(Path.GetTempPath(), "tickets.journal.jsonl"),
                ["EnrollmentBroker:Storage:JournalHmacKey"] = Convert.ToBase64String(new byte[32]),
                ["EnrollmentBroker:Headscale:ApiBaseUrl"] = "https://headscale-api.stayactive.test",
                ["EnrollmentBroker:Headscale:UserId"] = "1",
                ["EnrollmentBroker:Headscale:LoginServer"] = "https://headscale.stayactive.test"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            EnrollmentBrokerSettings.Load(configuration, new TestWebHostEnvironment("Testing", Path.GetTempPath())));

        Assert.Contains(EnrollmentBrokerSettings.HeadscaleControllerApiBaseUrl, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_settings_fail_closed_when_development_headers_or_secret_files_are_requested_insecurely()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnrollmentBroker:LocalDevelopment:Enabled"] = "true"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            EnrollmentBrokerSettings.Load(configuration, new TestWebHostEnvironment("Production", Path.GetTempPath())));

        Assert.Contains("forbidden in Production", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string environmentName, string contentRootPath)
        {
            EnvironmentName = environmentName;
            ContentRootPath = contentRootPath;
        }

        public string ApplicationName { get; set; } = "EnrollmentBroker.Tests";

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = string.Empty;

        public string EnvironmentName { get; set; }

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
