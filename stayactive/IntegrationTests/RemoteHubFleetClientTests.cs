using System.Net;
using System.Net.Http;
using StayActive.Remotes;

namespace stayactive.IntegrationTests;

public sealed class RemoteHubFleetClientTests
{
    [Fact]
    public async Task RefreshAsync_UsesAuthenticatedHttpsFleetEndpoint_AndParsesOnlyAllowedMetadata()
    {
        var tokenProvider = new FakeTokenProvider("test-access-token");
        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """
            {
              "devices": [
                {
                  "headscaleNodeId": "node:office",
                  "ownerDisplayNameOptIn": true,
                  "ownerDisplayName": "Alice",
                  "coarseLocationOptIn": true,
                  "coarseLocation": "Austin office",
                  "meshCentralNodeId": "node//office",
                  "verified": true,
                  "allowedCapabilities": ["ExitNode", "ScreenView", "SendFile"],
                  "version": 2,
                  "updatedAtUtc": "2026-07-16T12:00:00Z"
                }
              ]
            }
            """));
        using var client = new RemoteHubFleetClient(
            () => Preferences("https://remotes.example.test/center"),
            tokenProvider,
            new HttpClient(handler));

        await client.RefreshAsync(CancellationToken.None);

        var snapshot = client.GetCachedSnapshot();
        var device = Assert.Single(snapshot.Devices).Value;
        Assert.Equal(RemoteHubInventoryState.Available, snapshot.State);
        Assert.Equal("Alice", device.OwnerDisplayName);
        Assert.Equal("Austin office", device.CoarseLocation);
        Assert.Equal("node//office", device.MeshCentralNodeId);
        Assert.Equal(RemoteCapability.ExitNode | RemoteCapability.ScreenView | RemoteCapability.SendFile, device.AllowedCapabilities);
        Assert.Equal("https://remotes.example.test/center/api/v1/fleet", handler.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal(1, tokenProvider.GetAccessTokenCallCount);
    }

    [Theory]
    [InlineData("http://remotes.example.test")]
    [InlineData("https://login.tailscale.com")]
    [InlineData("https://login.tailscale.com.")]
    public async Task RefreshAsync_WhenTheRemoteHubUrlIsUnsafe_DoesNotRequestATokenOrOpenANetworkConnection(string remoteHubUrl)
    {
        var tokenProvider = new FakeTokenProvider("test-access-token");
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("No HTTP request should occur."));
        using var client = new RemoteHubFleetClient(
            () => Preferences(remoteHubUrl),
            tokenProvider,
            new HttpClient(handler));

        await client.RefreshAsync(CancellationToken.None);

        Assert.Equal(RemoteHubInventoryState.NotConfigured, client.GetCachedSnapshot().State);
        Assert.Equal(0, tokenProvider.GetAccessTokenCallCount);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task RefreshAsync_WhenTheServerReturnsUnauthorized_FailsClosed()
    {
        var tokenProvider = new FakeTokenProvider("test-access-token");
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new RemoteHubFleetClient(
            () => Preferences("https://remotes.example.test"),
            tokenProvider,
            new HttpClient(handler));

        await client.RefreshAsync(CancellationToken.None);

        var snapshot = client.GetCachedSnapshot();
        Assert.Equal(RemoteHubInventoryState.AuthenticationRequired, snapshot.State);
        Assert.Empty(snapshot.Devices);
    }

    [Fact]
    public async Task RefreshAsync_WhenTheServerRedirects_FailsClosedWithoutFollowingTheRedirect()
    {
        var tokenProvider = new FakeTokenProvider("test-access-token");
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://unexpected.example.test/fleet") }
        });
        using var client = new RemoteHubFleetClient(
            () => Preferences("https://remotes.example.test"),
            tokenProvider,
            new HttpClient(handler));

        await client.RefreshAsync(CancellationToken.None);

        Assert.Equal(RemoteHubInventoryState.Unavailable, client.GetCachedSnapshot().State);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_WhenInventoryContainsAnUnknownCapability_FailsClosed()
    {
        var tokenProvider = new FakeTokenProvider("test-access-token");
        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """
            {
              "devices": [
                {
                  "headscaleNodeId": "node:office",
                  "ownerDisplayNameOptIn": false,
                  "ownerDisplayName": null,
                  "coarseLocationOptIn": false,
                  "coarseLocation": null,
                  "meshCentralNodeId": null,
                  "verified": true,
                  "allowedCapabilities": ["ScreenView", "UnsafeCapability"],
                  "version": 1,
                  "updatedAtUtc": "2026-07-16T12:00:00Z"
                }
              ]
            }
            """));
        using var client = new RemoteHubFleetClient(
            () => Preferences("https://remotes.example.test"),
            tokenProvider,
            new HttpClient(handler));

        await client.RefreshAsync(CancellationToken.None);

        Assert.Equal(RemoteHubInventoryState.Unavailable, client.GetCachedSnapshot().State);
        Assert.Empty(client.GetCachedSnapshot().Devices);
    }

    [Fact]
    public async Task RefreshAsync_WhenTheServerReturnsMetadataWithoutItsOptIn_FailsClosed()
    {
        var tokenProvider = new FakeTokenProvider("test-access-token");
        var handler = new StubHttpMessageHandler(_ => JsonResponse(
            """
            {
              "devices": [
                {
                  "headscaleNodeId": "node:office",
                  "ownerDisplayNameOptIn": false,
                  "ownerDisplayName": "Alice",
                  "coarseLocationOptIn": false,
                  "coarseLocation": null,
                  "meshCentralNodeId": null,
                  "verified": true,
                  "allowedCapabilities": [],
                  "version": 1,
                  "updatedAtUtc": "2026-07-16T12:00:00Z"
                }
              ]
            }
            """));
        using var client = new RemoteHubFleetClient(
            () => Preferences("https://remotes.example.test"),
            tokenProvider,
            new HttpClient(handler));

        await client.RefreshAsync(CancellationToken.None);

        Assert.Equal(RemoteHubInventoryState.Unavailable, client.GetCachedSnapshot().State);
        Assert.Empty(client.GetCachedSnapshot().Devices);
    }

    private static RemoteClientPreferences Preferences(string remoteHubUrl)
    {
        return new RemoteClientPreferences(
            "https://headscale.example.test",
            remoteHubUrl,
            "https://remotes.example.test/admin",
            "https://mesh.example.test",
            "Local PC",
            "Test lab");
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };
    }

    private sealed class FakeTokenProvider(string token) : IRemoteHubAccessTokenProvider
    {
        public int GetAccessTokenCallCount { get; private set; }

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            GetAccessTokenCallCount++;
            return Task.FromResult(token);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            return Task.FromResult(responseFactory(request));
        }
    }
}
