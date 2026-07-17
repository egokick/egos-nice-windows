using System.Net;
using System.Net.Http;
using System.Text.Json;
using StayActive.Remotes;

namespace stayactive.IntegrationTests;

public sealed class RemoteEnrollmentClientTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 16, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IssueAsync_UsesFixedPolicyRequestAndBuildsAOneTimeLocalCommand()
    {
        const string authKey = "tskey-auth-test-secret";
        var tokens = new FakeEnrollmentAccessTokenProvider("fresh-enrollment-token");
        var handler = new StubHttpMessageHandler(_ => JsonResponse(HttpStatusCode.Created, TicketEnvelope(
            "device",
            "issued",
            "2026-07-16T18:15:00Z",
            "https://headscale.example.test",
            "[\"tag:stayactive\"]",
            authKey)));
        using var client = new RemoteEnrollmentClient(
            Preferences,
            tokens,
            new HttpClient(handler),
            utcNow: () => FixedNow);

        var issued = await client.IssueAsync(RemoteEnrollmentKind.Device, CancellationToken.None);

        Assert.Equal(1, tokens.CallCount);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://enrollment.example.test/center/api/v1/enrollment-tickets", handler.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("device", request.RootElement.GetProperty("kind").GetString());
        Assert.Equal(RemoteEnrollmentClient.FixedLifetimeMinutes, request.RootElement.GetProperty("lifetimeMinutes").GetInt32());
        Assert.False(request.RootElement.TryGetProperty("tags", out _));
        Assert.Equal("tailscale up --login-server https://headscale.example.test --auth-key " + authKey, issued.JoinCommand);
        Assert.Equal(RemoteEnrollmentTicketState.Issued, issued.Ticket.State);
        Assert.DoesNotContain(authKey, issued.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(authKey, issued.Ticket.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueAsync_ExitNodeAcceptsOnlyBrokerFixedTags()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse(HttpStatusCode.Created, TicketEnvelope(
            "exitNode",
            "issued",
            "2026-07-16T18:15:00Z",
            "https://headscale.example.test",
            "[\"tag:stayactive\",\"tag:stayactive-exit\"]",
            "tskey-auth-exit")));
        using var client = new RemoteEnrollmentClient(
            Preferences,
            new FakeEnrollmentAccessTokenProvider("fresh-enrollment-token"),
            new HttpClient(handler),
            utcNow: () => FixedNow);

        var issued = await client.IssueAsync(RemoteEnrollmentKind.ExitNode, CancellationToken.None);

        Assert.Equal(RemoteEnrollmentKind.ExitNode, issued.Ticket.Kind);
        Assert.Equal(new[] { "tag:stayactive", "tag:stayactive-exit" }, issued.Ticket.AdvertiseTags);
        Assert.DoesNotContain("advertise-tags", issued.JoinCommand, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IssueAsync_WhenBrokerUrlIsUnsafe_DoesNotSignInOrSendAnything()
    {
        var tokens = new FakeEnrollmentAccessTokenProvider("must-not-be-used");
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("No request should be sent."));
        using var client = new RemoteEnrollmentClient(
            () => Preferences() with { RemoteEnrollmentUrl = "http://enrollment.example.test" },
            tokens,
            new HttpClient(handler),
            utcNow: () => FixedNow);

        var exception = await Assert.ThrowsAsync<RemoteEnrollmentException>(
            () => client.IssueAsync(RemoteEnrollmentKind.Device, CancellationToken.None));

        Assert.Equal("Configure an HTTPS URL for your self-hosted enrollment broker first.", exception.Message);
        Assert.Equal(0, tokens.CallCount);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetStatusAsync_WhenServerReturnsAnAuthKey_FailsClosed()
    {
        const string leakedKey = "tskey-auth-must-never-be-shown";
        var handler = new StubHttpMessageHandler(_ => JsonResponse(HttpStatusCode.OK, TicketEnvelope(
            "device",
            "issued",
            "2026-07-16T18:15:00Z",
            "https://headscale.example.test",
            "[\"tag:stayactive\"]",
            leakedKey)));
        using var client = new RemoteEnrollmentClient(
            Preferences,
            new FakeEnrollmentAccessTokenProvider("fresh-enrollment-token"),
            new HttpClient(handler),
            utcNow: () => FixedNow);

        var exception = await Assert.ThrowsAsync<RemoteEnrollmentException>(
            () => client.GetStatusAsync("b3d0e0b6-6d55-4eab-9c4d-a0a9f1fe3aab", CancellationToken.None));

        Assert.Equal("The enrollment broker returned an invalid ticket status.", exception.Message);
        Assert.DoesNotContain(leakedKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokeAsync_WhenTicketWasRedeemed_ReturnsOnlySafeUsedStatus()
    {
        const string ticketId = "b3d0e0b6-6d55-4eab-9c4d-a0a9f1fe3aab";
        var body =
            """
            {
              "error": "ticket_already_redeemed",
              "ticket": {
                "id": "b3d0e0b6-6d55-4eab-9c4d-a0a9f1fe3aab",
                "kind": "device",
                "status": "redeemed",
                "expiresAtUtc": "2026-07-16T18:15:00Z",
                "loginServer": "https://headscale.example.test",
                "advertiseTags": ["tag:stayactive"]
              }
            }
            """;
        var handler = new StubHttpMessageHandler(_ => JsonResponse(HttpStatusCode.Conflict, body));
        using var client = new RemoteEnrollmentClient(
            Preferences,
            new FakeEnrollmentAccessTokenProvider("fresh-enrollment-token"),
            new HttpClient(handler),
            utcNow: () => FixedNow);

        var ticket = await client.RevokeAsync(ticketId, CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal(RemoteEnrollmentTicketState.Used, ticket.State);
        Assert.Equal(ticketId, ticket.Id);
    }

    [Fact]
    public async Task IssueAsync_RejectsTailscaleHostedLoginServer()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse(HttpStatusCode.Created, TicketEnvelope(
            "device",
            "issued",
            "2026-07-16T18:15:00Z",
            "https://login.tailscale.com",
            "[\"tag:stayactive\"]",
            "tskey-auth-test")));
        using var client = new RemoteEnrollmentClient(
            Preferences,
            new FakeEnrollmentAccessTokenProvider("fresh-enrollment-token"),
            new HttpClient(handler),
            utcNow: () => FixedNow);

        var exception = await Assert.ThrowsAsync<RemoteEnrollmentException>(
            () => client.IssueAsync(RemoteEnrollmentKind.Device, CancellationToken.None));

        Assert.Equal("The enrollment broker returned an invalid one-time enrollment response.", exception.Message);
    }

    [Fact]
    public async Task IssueAsync_RejectsAnUnsafeAuthKeyBeforeBuildingACopyableCommand()
    {
        const string unsafeKey = "tskey-auth-safe;unexpected-command";
        var handler = new StubHttpMessageHandler(_ => JsonResponse(HttpStatusCode.Created, TicketEnvelope(
            "device",
            "issued",
            "2026-07-16T18:15:00Z",
            "https://headscale.example.test",
            "[\"tag:stayactive\"]",
            unsafeKey)));
        using var client = new RemoteEnrollmentClient(
            Preferences,
            new FakeEnrollmentAccessTokenProvider("fresh-enrollment-token"),
            new HttpClient(handler),
            utcNow: () => FixedNow);

        var exception = await Assert.ThrowsAsync<RemoteEnrollmentException>(
            () => client.IssueAsync(RemoteEnrollmentKind.Device, CancellationToken.None));

        Assert.Equal("The enrollment broker returned an invalid one-time enrollment response.", exception.Message);
        Assert.DoesNotContain(unsafeKey, exception.ToString(), StringComparison.Ordinal);
    }
    private static RemoteClientPreferences Preferences()
    {
        return new RemoteClientPreferences(
            "https://headscale.example.test",
            "https://remotehub.example.test",
            "https://remotehub.example.test/admin",
            "https://mesh.example.test",
            "Local PC",
            "Test lab",
            "https://issuer.example.test",
            "stayactive-remotes-fleet",
            "https://enrollment.example.test/center",
            "stayactive-remotes-enrollment");
    }

    private static string TicketEnvelope(
        string kind,
        string status,
        string expiresAtUtc,
        string loginServer,
        string tagsJson,
        string authKey)
    {
        var tags = JsonSerializer.Deserialize<string[]>(tagsJson)
            ?? throw new InvalidOperationException("Test tags could not be parsed.");
        return JsonSerializer.Serialize(new
        {
            ticket = new
            {
                id = "b3d0e0b6-6d55-4eab-9c4d-a0a9f1fe3aab",
                kind,
                status,
                expiresAtUtc,
                loginServer,
                advertiseTags = tags
            },
            authKey
        });
    }
    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json)
        };
    }

    private sealed class FakeEnrollmentAccessTokenProvider(string token) : IRemoteEnrollmentAccessTokenProvider
    {
        public int CallCount { get; private set; }

        public Task<string> GetFreshAccessTokenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(token);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}