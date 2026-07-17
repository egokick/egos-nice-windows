using System.Net;
using System.Security.Cryptography;
using System.Text;
using StayActive.Remotes;

namespace stayactive.IntegrationTests;

public sealed class RemoteHubOidcAccessTokenProviderTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 16, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SignInAsync_UsesS256PkceAndStoresTheIssuedToken()
    {
        var storage = new FakeTokenStorage();
        var http = new FakeOidcHttpClient
        {
            DiscoveryResponse = DiscoveryResponse(),
            TokenResponse = new RemoteHubOidcHttpResponse(
                200,
                """
                {
                  "access_token": "issued-access-token",
                  "refresh_token": "issued-refresh-token",
                  "token_type": "Bearer",
                  "expires_in": 3600
                }
                """)
        };
        var listener = new FakeLoopbackCallbackListener(new Uri("http://127.0.0.1:41231/"));
        var browser = new FakeBrowser(uri =>
        {
            var query = ParseQuery(uri);
            listener.Callback = new Uri(
                listener.RedirectUri.AbsoluteUri +
                "?code=one-time-code&state=" + Uri.EscapeDataString(query["state"]));
        });
        using var provider = CreateProvider(storage, http, browser, listener);

        await provider.SignInAsync(CancellationToken.None);

        var authorizationQuery = ParseQuery(Assert.IsType<Uri>(browser.OpenedUri));
        Assert.Equal("code", authorizationQuery["response_type"]);
        Assert.Equal("stayactive-desktop", authorizationQuery["client_id"]);
        Assert.Equal(RemoteHubOidcConfiguration.RequiredScopes, authorizationQuery["scope"]);
        Assert.Equal("S256", authorizationQuery["code_challenge_method"]);
        Assert.NotEmpty(authorizationQuery["state"]);

        var form = Assert.Single(http.PostedForms);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("one-time-code", form["code"]);
        Assert.Equal(listener.RedirectUri.AbsoluteUri, form["redirect_uri"]);
        Assert.Equal("stayactive-desktop", form["client_id"]);
        Assert.Equal(
            CreatePkceChallenge(form["code_verifier"]),
            authorizationQuery["code_challenge"]);

        var stored = Assert.IsType<RemoteHubTokenSet>(storage.Value);
        Assert.Equal("issued-access-token", stored.AccessToken);
        Assert.Equal("issued-refresh-token", stored.RefreshToken);
        Assert.DoesNotContain("issued-access-token", stored.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("issued-refresh-token", stored.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignInAsync_EnrollmentConfigurationUsesFreshPkceWithoutOfflineAccess()
    {
        var storage = new FakeTokenStorage();
        var http = new FakeOidcHttpClient
        {
            DiscoveryResponse = DiscoveryResponse(),
            TokenResponse = new RemoteHubOidcHttpResponse(
                200,
                """
                {
                  "access_token": "enrollment-access-token",
                  "token_type": "Bearer",
                  "expires_in": 300
                }
                """)
        };
        var listener = new FakeLoopbackCallbackListener(new Uri("http://127.0.0.1:41236/"));
        var browser = new FakeBrowser(uri =>
        {
            var query = ParseQuery(uri);
            listener.Callback = new Uri(
                listener.RedirectUri.AbsoluteUri +
                "?code=enrollment-code&state=" + Uri.EscapeDataString(query["state"]));
        });
        Assert.True(RemoteHubOidcConfiguration.TryCreateEnrollment(
            "https://issuer.example.test",
            "stayactive-remotes-enrollment",
            out var configuration));
        using var provider = new RemoteHubOidcAccessTokenProvider(
            () => configuration,
            storage,
            http,
            browser,
            new FakeLoopbackCallbackListenerFactory(listener),
            requestTimeout: TimeSpan.FromSeconds(1),
            authorizationTimeout: TimeSpan.FromSeconds(1),
            utcNow: () => FixedNow);

        await provider.SignInAsync(CancellationToken.None);

        var query = ParseQuery(Assert.IsType<Uri>(browser.OpenedUri));
        Assert.Equal(RemoteHubOidcConfiguration.EnrollmentRequiredScopes, query["scope"]);
        Assert.Equal("login", query["prompt"]);
        Assert.Equal("0", query["max_age"]);
        Assert.DoesNotContain("offline_access", query["scope"], StringComparison.Ordinal);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Null(Assert.IsType<RemoteHubTokenSet>(storage.Value).RefreshToken);
    }

    [Fact]
    public async Task SignInAsync_EnrollmentConfigurationRejectsRefreshToken()
    {
        var storage = new FakeTokenStorage();
        var http = new FakeOidcHttpClient
        {
            DiscoveryResponse = DiscoveryResponse(),
            TokenResponse = new RemoteHubOidcHttpResponse(
                200,
                """
                {
                  "access_token": "enrollment-access-token",
                  "refresh_token": "unexpected-refresh-token",
                  "token_type": "Bearer",
                  "expires_in": 300
                }
                """)
        };
        var listener = new FakeLoopbackCallbackListener(new Uri("http://127.0.0.1:41237/"));
        var browser = new FakeBrowser(uri =>
        {
            var query = ParseQuery(uri);
            listener.Callback = new Uri(
                listener.RedirectUri.AbsoluteUri +
                "?code=enrollment-code&state=" + Uri.EscapeDataString(query["state"]));
        });
        Assert.True(RemoteHubOidcConfiguration.TryCreateEnrollment(
            "https://issuer.example.test",
            "stayactive-remotes-enrollment",
            out var configuration));
        using var provider = new RemoteHubOidcAccessTokenProvider(
            () => configuration,
            storage,
            http,
            browser,
            new FakeLoopbackCallbackListenerFactory(listener),
            requestTimeout: TimeSpan.FromSeconds(1),
            authorizationTimeout: TimeSpan.FromSeconds(1),
            utcNow: () => FixedNow);

        var exception = await Assert.ThrowsAsync<RemoteHubOidcException>(
            () => provider.SignInAsync(CancellationToken.None));

        Assert.Equal("The self-hosted identity service returned an invalid sign-in response.", exception.Message);
        Assert.Null(storage.Value);
        Assert.DoesNotContain("unexpected-refresh-token", exception.ToString(), StringComparison.Ordinal);
    }
    [Fact]
    public async Task GetAccessTokenAsync_RefreshesAnExpiringTokenAndPreservesUnrotatedRefreshToken()
    {
        var storage = new FakeTokenStorage
        {
            Value = new RemoteHubTokenSet(
                "https://issuer.example.test",
                "stayactive-desktop",
                "old-access-token",
                "saved-refresh-token",
                FixedNow.AddSeconds(10))
        };
        var http = new FakeOidcHttpClient
        {
            DiscoveryResponse = DiscoveryResponse(),
            TokenResponse = new RemoteHubOidcHttpResponse(
                200,
                """
                {
                  "access_token": "fresh-access-token",
                  "token_type": "bearer",
                  "expires_in": "3600"
                }
                """)
        };
        using var provider = CreateProvider(
            storage,
            http,
            new FakeBrowser(_ => { }),
            new FakeLoopbackCallbackListener(new Uri("http://127.0.0.1:41232/")));

        var accessToken = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("fresh-access-token", accessToken);
        var form = Assert.Single(http.PostedForms);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("saved-refresh-token", form["refresh_token"]);
        Assert.Equal("stayactive-desktop", form["client_id"]);
        var stored = Assert.IsType<RemoteHubTokenSet>(storage.Value);
        Assert.Equal("fresh-access-token", stored.AccessToken);
        Assert.Equal("saved-refresh-token", stored.RefreshToken);
        Assert.Equal(FixedNow.AddHours(1), stored.ExpiresAt);
    }

    [Fact]
    public async Task SignInAsync_WhenCallbackStateDoesNotMatch_DoesNotExchangeTheCode()
    {
        var storage = new FakeTokenStorage();
        var http = new FakeOidcHttpClient
        {
            DiscoveryResponse = DiscoveryResponse(),
            TokenResponse = new RemoteHubOidcHttpResponse(200, "{ \"access_token\": \"must-not-be-read\" }")
        };
        var listener = new FakeLoopbackCallbackListener(
            new Uri("http://127.0.0.1:41233/"))
        {
            Callback = new Uri("http://127.0.0.1:41233/?code=private-code&state=wrong-state")
        };
        using var provider = CreateProvider(storage, http, new FakeBrowser(_ => { }), listener);

        var exception = await Assert.ThrowsAsync<RemoteHubOidcException>(
            () => provider.SignInAsync(CancellationToken.None));

        Assert.Equal("The sign-in callback could not be verified.", exception.Message);
        Assert.Empty(http.PostedForms);
        Assert.Null(storage.Value);
        Assert.DoesNotContain("private-code", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenRefreshIsRejected_DoesNotLeakTokens()
    {
        const string savedRefreshToken = "saved-refresh-token-should-not-leak";
        const string responseSecret = "response-secret-should-not-leak";
        var storage = new FakeTokenStorage
        {
            Value = new RemoteHubTokenSet(
                "https://issuer.example.test",
                "stayactive-desktop",
                "expired-access-token-should-not-leak",
                savedRefreshToken,
                FixedNow.AddMinutes(-1))
        };
        var http = new FakeOidcHttpClient
        {
            DiscoveryResponse = DiscoveryResponse(),
            TokenResponse = new RemoteHubOidcHttpResponse(
                400,
                $"{{ \"error\": \"invalid_grant\", \"error_description\": \"{responseSecret}\" }}")
        };
        using var provider = CreateProvider(
            storage,
            http,
            new FakeBrowser(_ => { }),
            new FakeLoopbackCallbackListener(new Uri("http://127.0.0.1:41234/")));

        var exception = await Assert.ThrowsAsync<RemoteHubAuthenticationRequiredException>(
            () => provider.GetAccessTokenAsync(CancellationToken.None));

        Assert.True(storage.WasCleared);
        Assert.DoesNotContain(savedRefreshToken, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(responseSecret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(responseSecret, http.TokenResponse.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemLoopbackCallbackListener_UsesTheNarrowRegisteredRootPath()
    {
        using var listener = new SystemRemoteHubLoopbackCallbackListener();
        var redirectUri = listener.Start();

        Assert.Equal("http", redirectUri.Scheme);
        Assert.Equal("127.0.0.1", redirectUri.Host);
        Assert.Equal("/", redirectUri.AbsolutePath);

        var callbackTask = listener.WaitForCallbackAsync(CancellationToken.None);
        using var client = new HttpClient();
        using var response = await client.GetAsync(new Uri(redirectUri.AbsoluteUri + "?code=one-time-code&state=expected-state"));
        var callback = await callbackTask;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(redirectUri.AbsolutePath, callback.AbsolutePath);
        Assert.Equal(redirectUri.Port, callback.Port);
    }

    [Theory]
    [InlineData("http://issuer.example.test")]
    [InlineData("https://user:password@issuer.example.test")]
    [InlineData("https://issuer.example.test?redirect=https://attacker.example")]
    public void TryCreate_RejectsUnsafeIssuer(string issuer)
    {
        var accepted = RemoteHubOidcConfiguration.TryCreate(issuer, "stayactive-desktop", out _);

        Assert.False(accepted);
    }

    [Theory]
    [InlineData("https://login.tailscale.com")]
    [InlineData("https://login.tailscale.com.")]
    public void TryCreateEnrollment_RejectsTailscaleHostedIssuerBeforeOidcNetworkWork(string issuer)
    {
        var accepted = RemoteHubOidcConfiguration.TryCreateEnrollment(
            issuer,
            "stayactive-remotes-enrollment",
            out _);

        Assert.False(accepted);
    }
    [Fact]
    public async Task GetAccessTokenAsync_ObservesCallerCancellationBeforeNetworkWork()
    {
        var storage = new FakeTokenStorage();
        var http = new FakeOidcHttpClient { DiscoveryResponse = DiscoveryResponse() };
        using var provider = CreateProvider(
            storage,
            http,
            new FakeBrowser(_ => { }),
            new FakeLoopbackCallbackListener(new Uri("http://127.0.0.1:41235/")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetAccessTokenAsync(cancellation.Token));

        Assert.Empty(http.GetRequests);
        Assert.Empty(http.PostedForms);
    }

    [Fact]
    public async Task DpapiTokenStorage_RoundTripsOnlyEncryptedDataAndAtomicallyReplacesTheBlob()
    {
        const string firstAccessToken = "first-access-token-not-plaintext";
        const string secondAccessToken = "second-access-token-not-plaintext";
        var directory = Path.Combine(Path.GetTempPath(), "stayactive-oidc-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new DpapiCurrentUserRemoteHubTokenStorage(directory);
            await storage.SaveAsync(
                new RemoteHubTokenSet(
                    "https://issuer.example.test",
                    "stayactive-desktop",
                    firstAccessToken,
                    "first-refresh-token-not-plaintext",
                    FixedNow.AddHours(1)),
                CancellationToken.None);
            await storage.SaveAsync(
                new RemoteHubTokenSet(
                    "https://issuer.example.test",
                    "stayactive-desktop",
                    secondAccessToken,
                    "second-refresh-token-not-plaintext",
                    FixedNow.AddHours(2)),
                CancellationToken.None);

            var encryptedBlob = await File.ReadAllBytesAsync(Path.Combine(directory, "remotehub-oidc.tokens"));
            var stored = await storage.LoadAsync(CancellationToken.None);

            Assert.False(ContainsUtf8(encryptedBlob, firstAccessToken));
            Assert.False(ContainsUtf8(encryptedBlob, secondAccessToken));
            Assert.Equal(new[] { "remotehub-oidc.tokens" }, Directory.GetFiles(directory).Select(Path.GetFileName));
            Assert.Equal(secondAccessToken, Assert.IsType<RemoteHubTokenSet>(stored).AccessToken);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static RemoteHubOidcAccessTokenProvider CreateProvider(
        FakeTokenStorage storage,
        FakeOidcHttpClient http,
        FakeBrowser browser,
        FakeLoopbackCallbackListener listener)
    {
        return new RemoteHubOidcAccessTokenProvider(
            CreateConfiguration,
            storage,
            http,
            browser,
            new FakeLoopbackCallbackListenerFactory(listener),
            requestTimeout: TimeSpan.FromSeconds(1),
            authorizationTimeout: TimeSpan.FromSeconds(1),
            utcNow: () => FixedNow);
    }

    private static RemoteHubOidcConfiguration CreateConfiguration()
    {
        Assert.True(RemoteHubOidcConfiguration.TryCreate(
            "https://issuer.example.test",
            "stayactive-desktop",
            out var configuration));
        return configuration;
    }

    private static RemoteHubOidcHttpResponse DiscoveryResponse()
    {
        return new RemoteHubOidcHttpResponse(
            200,
            """
            {
              "issuer": "https://issuer.example.test",
              "authorization_endpoint": "https://issuer.example.test/authorize",
              "token_endpoint": "https://issuer.example.test/token"
            }
            """);
    }

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        return uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => Uri.UnescapeDataString(pair.Length == 2 ? pair[1] : string.Empty),
                StringComparer.Ordinal);
    }

    private static string CreatePkceChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool ContainsUtf8(byte[] haystack, string value)
    {
        return haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0;
    }

    private sealed class FakeTokenStorage : IRemoteHubTokenStorage
    {
        public RemoteHubTokenSet? Value { get; set; }

        public bool WasCleared { get; private set; }

        public Task<RemoteHubTokenSet?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Value);
        }

        public Task SaveAsync(RemoteHubTokenSet tokenSet, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Value = tokenSet;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCleared = true;
            Value = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOidcHttpClient : IRemoteHubOidcHttpClient
    {
        public RemoteHubOidcHttpResponse DiscoveryResponse { get; set; } = new(500, string.Empty);

        public RemoteHubOidcHttpResponse TokenResponse { get; set; } = new(500, string.Empty);

        public List<Uri> GetRequests { get; } = new();

        public List<IReadOnlyDictionary<string, string>> PostedForms { get; } = new();

        public Task<RemoteHubOidcHttpResponse> GetAsync(Uri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetRequests.Add(uri);
            return Task.FromResult(DiscoveryResponse);
        }

        public Task<RemoteHubOidcHttpResponse> PostFormAsync(
            Uri uri,
            IReadOnlyDictionary<string, string> formValues,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PostedForms.Add(new Dictionary<string, string>(formValues, StringComparer.Ordinal));
            return Task.FromResult(TokenResponse);
        }
    }

    private sealed class FakeBrowser(Action<Uri> onOpen) : IRemoteHubOidcBrowser
    {
        public Uri? OpenedUri { get; private set; }

        public Task OpenAsync(Uri authorizationUri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenedUri = authorizationUri;
            onOpen(authorizationUri);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLoopbackCallbackListenerFactory(FakeLoopbackCallbackListener listener)
        : IRemoteHubLoopbackCallbackListenerFactory
    {
        public IRemoteHubLoopbackCallbackListener Create() => listener;
    }

    private sealed class FakeLoopbackCallbackListener(Uri redirectUri) : IRemoteHubLoopbackCallbackListener
    {
        public Uri RedirectUri { get; } = redirectUri;

        public Uri? Callback { get; set; }

        public Uri Start() => RedirectUri;

        public Task<Uri> WaitForCallbackAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Callback ?? throw new InvalidOperationException("A test callback was not configured."));
        }

        public void Dispose()
        {
        }
    }
}
