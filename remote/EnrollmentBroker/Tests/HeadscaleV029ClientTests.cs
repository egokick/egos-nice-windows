using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StayActive.EnrollmentBroker.Services;

namespace StayActive.EnrollmentBroker.Tests;

public sealed class HeadscaleV029ClientTests
{
    [Fact]
    public async Task Create_uses_the_version_029_single_use_pre_auth_key_contract()
    {
        var expiration = DateTimeOffset.UtcNow.AddMinutes(15);
        var createResponseBody = JsonSerializer.Serialize(new
        {
            preAuthKey = new
            {
                id = "42",
                key = "hskey-auth-one-time-secret",
                reusable = false,
                ephemeral = false,
                used = false,
                expiration,
                aclTags = new[] { "tag:stayactive", "tag:stayactive-exit" }
            }
        });
        var handler = new RecordingHandler(_ => JsonResponse(createResponseBody));
        using var client = CreateClient(handler);

        var created = await client.CreateOneUsePreAuthKeyAsync(
            "7",
            expiration,
            ["tag:stayactive", "tag:stayactive-exit"],
            CancellationToken.None);

        Assert.Equal("42", created.Id);
        Assert.Equal("hskey-auth-one-time-secret", created.RawKey);
        Assert.False(created.Reusable);
        Assert.False(created.Ephemeral);
        Assert.False(created.Used);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://headscale-api.stayactive.test/api/v1/preauthkey", request.Uri);
        Assert.Equal("Bearer hskey-api-unit-test-secret", request.Authorization);
        Assert.Contains("\"user\":\"7\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"reusable\":false", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"ephemeral\":false", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"aclTags\":[\"tag:stayactive\",\"tag:stayactive-exit\"]", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_ignores_the_list_response_key_field_and_expire_uses_the_safe_endpoint()
    {
        const string redactedListValue = "hskey-auth-should-not-be-retained";
        var expiration = DateTimeOffset.UtcNow.AddMinutes(15);
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var listResponseBody = JsonSerializer.Serialize(new
                {
                    preAuthKeys = new[]
                    {
                        new
                        {
                            id = "42",
                            key = redactedListValue,
                            reusable = false,
                            ephemeral = false,
                            used = false,
                            expiration,
                            aclTags = new[] { "tag:stayactive" }
                        }
                    }
                });
                return JsonResponse(listResponseBody);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = CreateClient(handler);

        var status = await client.GetPreAuthKeyStatusAsync("42", CancellationToken.None);
        await client.ExpirePreAuthKeyAsync("42", CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("42", status!.Id);
        Assert.DoesNotContain(redactedListValue, status.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count);
        var expire = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, expire.Method);
        Assert.Equal("https://headscale-api.stayactive.test/api/v1/preauthkey/expire", expire.Uri);
        Assert.Equal("{\"id\":\"42\"}", expire.Body);
    }

    [Fact]
    public async Task Transport_failures_are_mapped_to_a_safe_upstream_exception()
    {
        const string sensitiveTransportMessage = "hskey-auth-never-in-transport-exception";
        var handler = new RecordingHandler(_ => throw new HttpRequestException(sensitiveTransportMessage));
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<HeadscaleUnavailableException>(() =>
            client.ExpirePreAuthKeyAsync("42", CancellationToken.None));

        Assert.DoesNotContain(sensitiveTransportMessage, exception.ToString(), StringComparison.Ordinal);
    }
    [Fact]
    public async Task Upstream_errors_do_not_reflect_a_response_body_that_might_contain_a_key()
    {
        const string sensitiveBody = "hskey-auth-never-in-exception";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(sensitiveBody, Encoding.UTF8, "text/plain")
        });
        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<HeadscaleApiException>(() =>
            client.ExpirePreAuthKeyAsync("42", CancellationToken.None));

        Assert.Equal((int)HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.DoesNotContain(sensitiveBody, exception.ToString(), StringComparison.Ordinal);
    }

    private static HeadscaleV029Client CreateClient(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("https://headscale-api.stayactive.test/")
            },
            "hskey-api-unit-test-secret");

    private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString() ?? string.Empty,
                body));
            return _responseFactory(request);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string Authorization, string Body);
}
