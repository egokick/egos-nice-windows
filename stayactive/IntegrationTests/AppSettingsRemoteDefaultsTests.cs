using StayActive;
using StayActive.Remotes;

namespace stayactive.IntegrationTests;

public sealed class AppSettingsRemoteDefaultsTests
{
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