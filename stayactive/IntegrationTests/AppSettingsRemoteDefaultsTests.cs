using System.Text.Json;
using StayActive;
using StayActive.Remotes;

namespace stayactive.IntegrationTests;

public sealed class AppSettingsRemoteDefaultsTests
{
    [Fact]
    public void NewSettings_HaveNoRemoteDeviceDisplayOverrides()
    {
        var settings = new AppSettings();

        Assert.NotNull(settings.RemoteDeviceDisplayOverrides);
        Assert.Empty(settings.RemoteDeviceDisplayOverrides);
    }

    [Fact]
    public void Clone_DeepCopiesRemoteDeviceDisplayOverrides()
    {
        var settings = new AppSettings
        {
            RemoteDeviceDisplayOverrides = new Dictionary<string, RemoteDeviceDisplayOverride>(StringComparer.Ordinal)
            {
                ["node-123"] = new RemoteDeviceDisplayOverride
                {
                    OwnerOrUserLabel = "Alice",
                    Location = "London"
                }
            }
        };

        var clone = settings.Clone();

        Assert.NotSame(settings.RemoteDeviceDisplayOverrides, clone.RemoteDeviceDisplayOverrides);
        Assert.True(clone.RemoteDeviceDisplayOverrides.TryGetValue("node-123", out var clonedOverride));
        Assert.NotSame(settings.RemoteDeviceDisplayOverrides["node-123"], clonedOverride);
        Assert.Equal("Alice", clonedOverride.OwnerOrUserLabel);
        Assert.Equal("London", clonedOverride.Location);

        clonedOverride.OwnerOrUserLabel = "Bob";
        clonedOverride.Location = "Paris";
        clone.RemoteDeviceDisplayOverrides["node-456"] = new RemoteDeviceDisplayOverride();

        Assert.Equal("Alice", settings.RemoteDeviceDisplayOverrides["node-123"].OwnerOrUserLabel);
        Assert.Equal("London", settings.RemoteDeviceDisplayOverrides["node-123"].Location);
        Assert.False(settings.RemoteDeviceDisplayOverrides.ContainsKey("node-456"));
    }

    [Fact]
    public void RemoteDeviceDisplayOverrides_RoundTripByStableDeviceId()
    {
        var settings = new AppSettings
        {
            RemoteDeviceDisplayOverrides = new Dictionary<string, RemoteDeviceDisplayOverride>(StringComparer.Ordinal)
            {
                ["node:travel-laptop"] = new("Alice", "Home office")
            }
        };

        var roundTripped = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));
        Assert.NotNull(roundTripped);
        roundTripped.NormalizeRemoteDeviceDisplayOverrides();

        var displayOverride = Assert.Single(roundTripped.RemoteDeviceDisplayOverrides);
        Assert.Equal("node:travel-laptop", displayOverride.Key);
        Assert.Equal("Alice", displayOverride.Value.OwnerOrUserLabel);
        Assert.Equal("Home office", displayOverride.Value.Location);
    }

    [Fact]
    public void NormalizeRemoteDeviceDisplayOverrides_RepairsExplicitNullFromLegacyJson()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"RemoteDeviceDisplayOverrides\":null}");
        Assert.NotNull(settings);

        settings.NormalizeRemoteDeviceDisplayOverrides();

        Assert.NotNull(settings.RemoteDeviceDisplayOverrides);
        Assert.Empty(settings.RemoteDeviceDisplayOverrides);
    }

    [Fact]
    public void NewSettings_PinTheSelfHostedLanEndpoints()
    {
        var settings = new AppSettings();

        Assert.Equal(StayActiveRemoteDefaults.ControlPlaneUrl, settings.RemoteControlPlaneUrl);
        Assert.Equal(StayActiveRemoteDefaults.RemoteHubUrl, settings.RemoteHubUrl);
        Assert.Equal(StayActiveRemoteDefaults.AdminConsoleUrl, settings.RemoteAdminConsoleUrl);
        Assert.Equal(StayActiveRemoteDefaults.MeshCentralUrl, settings.RemoteMeshCentralUrl);
        Assert.Equal(StayActiveRemoteDefaults.OidcIssuerUrl, settings.RemoteHubOidcIssuerUrl);
        Assert.Equal(StayActiveRemoteDefaults.FleetOidcClientId, settings.RemoteHubOidcClientId);
        Assert.Equal(StayActiveRemoteDefaults.EnrollmentBrokerUrl, settings.RemoteEnrollmentUrl);
        Assert.Equal(StayActiveRemoteDefaults.EnrollmentOidcClientId, settings.RemoteEnrollmentOidcClientId);
    }

    [Fact]
    public void AllBlankLegacySettings_AreMovedToTheSelfHostedLanDefaults()
    {
        var settings = new AppSettings
        {
            RemoteControlPlaneUrl = string.Empty,
            RemoteHubUrl = string.Empty,
            RemoteAdminConsoleUrl = string.Empty,
            RemoteMeshCentralUrl = string.Empty,
            RemoteHubOidcIssuerUrl = string.Empty,
            RemoteHubOidcClientId = string.Empty,
            RemoteEnrollmentUrl = string.Empty,
            RemoteEnrollmentOidcClientId = string.Empty
        };

        settings.ApplySelfHostedRemoteDefaultsIfUnconfigured();

        Assert.Equal(StayActiveRemoteDefaults.ControlPlaneUrl, settings.RemoteControlPlaneUrl);
        Assert.Equal(StayActiveRemoteDefaults.RemoteHubUrl, settings.RemoteHubUrl);
        Assert.Equal(StayActiveRemoteDefaults.EnrollmentBrokerUrl, settings.RemoteEnrollmentUrl);
        Assert.Equal(StayActiveRemoteDefaults.EnrollmentOidcClientId, settings.RemoteEnrollmentOidcClientId);
    }

    [Fact]
    public void PartialExistingRemoteConfiguration_IsNotOverwritten()
    {
        const string ownerConfiguredRemoteHub = "https://remotehub.owner.example";
        var settings = new AppSettings
        {
            RemoteControlPlaneUrl = string.Empty,
            RemoteHubUrl = ownerConfiguredRemoteHub,
            RemoteAdminConsoleUrl = string.Empty,
            RemoteMeshCentralUrl = string.Empty,
            RemoteHubOidcIssuerUrl = string.Empty,
            RemoteHubOidcClientId = string.Empty,
            RemoteEnrollmentUrl = string.Empty,
            RemoteEnrollmentOidcClientId = string.Empty
        };

        settings.ApplySelfHostedRemoteDefaultsIfUnconfigured();

        Assert.Equal(ownerConfiguredRemoteHub, settings.RemoteHubUrl);
        Assert.Equal(string.Empty, settings.RemoteEnrollmentUrl);
    }
}
