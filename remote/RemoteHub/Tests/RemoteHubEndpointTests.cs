using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StayActive.RemoteHub.Auth;
using StayActive.RemoteHub.Domain;

namespace StayActive.RemoteHub.Tests;

public sealed class RemoteHubEndpointTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "stayactive-remotehub-endpoints", Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private WebApplication? _app;

    [Fact]
    public async Task Fleet_and_admin_endpoints_require_scopes_and_return_only_verified_devices()
    {
        var client = await StartClientAsync();
        try
        {

            using (var unauthenticated = await client.GetAsync("/api/v1/fleet"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            }

            var pending = new InventoryUpdateRequest(
                ExpectedVersion: 0,
                OwnerDisplayNameOptIn: true,
                OwnerDisplayName: "Pending owner",
                CoarseLocationOptIn: false,
                CoarseLocation: null,
                MeshCentralNodeId: null,
                Verified: false,
                AllowedCapabilities: [RemoteCapability.ExitNode]);
            using var pendingResponse = await PutInventoryAsync(client, "node-pending", pending);
            Assert.Equal(HttpStatusCode.Created, pendingResponse.StatusCode);

            var verified = new InventoryUpdateRequest(
                ExpectedVersion: 0,
                OwnerDisplayNameOptIn: true,
                OwnerDisplayName: "Riley",
                CoarseLocationOptIn: true,
                CoarseLocation: "Austin, TX",
                MeshCentralNodeId: "mesh-riley",
                Verified: true,
                AllowedCapabilities: [RemoteCapability.ExitNode, RemoteCapability.ScreenView]);
            using var verifiedResponse = await PutInventoryAsync(client, "node-verified", verified);
            Assert.Equal(HttpStatusCode.Created, verifiedResponse.StatusCode);

            using var fleetRequest = CreateRequest(HttpMethod.Get, "/api/v1/fleet", "remotehub.fleet.read");
            using var fleetResponse = await client.SendAsync(fleetRequest);
            Assert.Equal(HttpStatusCode.OK, fleetResponse.StatusCode);
            using var fleetDocument = JsonDocument.Parse(await fleetResponse.Content.ReadAsStringAsync());
            var fleetDevices = fleetDocument.RootElement.GetProperty("devices");
            var fleetDevice = Assert.Single(fleetDevices.EnumerateArray());
            Assert.Equal("node-verified", fleetDevice.GetProperty("headscaleNodeId").GetString());
            Assert.True(fleetDevice.GetProperty("verified").GetBoolean());
            Assert.Equal("Riley", fleetDevice.GetProperty("ownerDisplayName").GetString());

            using var auditRequest = CreateRequest(HttpMethod.Get, "/api/v1/admin/audit?take=10", "remotehub.inventory.write");
            using var auditResponse = await client.SendAsync(auditRequest);
            Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
            using var auditDocument = JsonDocument.Parse(await auditResponse.Content.ReadAsStringAsync());
            Assert.Equal(2, auditDocument.RootElement.GetProperty("events").GetArrayLength());
        }
        finally
        {
            client.Dispose();
            await StopAndCleanUpAsync();
        }
    }

    [Fact]
    public async Task Admin_endpoints_require_the_dedicated_administrator_role()
    {
        var client = await StartClientAsync();
        try
        {
            using var request = CreateRequest(
                HttpMethod.Get,
                "/api/v1/admin/audit?take=10",
                "remotehub.inventory.write",
                roles: string.Empty);

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            client.Dispose();
            await StopAndCleanUpAsync();
        }
    }

    [Fact]
    public async Task Admin_endpoint_rejects_numeric_capability_values()
    {
        var client = await StartClientAsync();
        try
        {
            using var request = CreateRequest(HttpMethod.Put, "/api/v1/admin/inventory/node-numeric", "remotehub.inventory.write");
            request.Content = new StringContent(
                """
                {"expectedVersion":0,"ownerDisplayNameOptIn":false,"coarseLocationOptIn":false,"verified":true,"allowedCapabilities":[1]}
                """,
                System.Text.Encoding.UTF8,
                "application/json");

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            client.Dispose();
            await StopAndCleanUpAsync();
        }
    }

    private async Task StopAndCleanUpAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        CryptographicOperations.ZeroMemory(_key);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task<HttpClient> StartClientAsync()
    {
        Directory.CreateDirectory(_directory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(RemoteHubApplication).Assembly.FullName
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteHub:LocalDevelopment:Enabled"] = "true",
            ["RemoteHub:Storage:JournalPath"] = Path.Combine(_directory, "inventory.journal.jsonl"),
            ["RemoteHub:Storage:JournalHmacKey"] = Convert.ToBase64String(_key)
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = RemoteHubApplication.Build(builder);
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses;
        var address = Assert.Single(addresses);
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private static async Task<HttpResponseMessage> PutInventoryAsync(
        HttpClient client,
        string nodeId,
        InventoryUpdateRequest requestBody)
    {
        using var request = CreateRequest(HttpMethod.Put, $"/api/v1/admin/inventory/{nodeId}", "remotehub.inventory.write");
        request.Content = JsonContent.Create(requestBody, options: RemoteHubJson.CreateOptions());
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        string scopes,
        string roles = "stayactive.remotehub.admin")
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(DevelopmentHeaderAuthenticationHandler.SubjectHeader, "test-operator");
        request.Headers.Add(DevelopmentHeaderAuthenticationHandler.ScopesHeader, scopes);
        if (!string.IsNullOrWhiteSpace(roles))
        {
            request.Headers.Add(DevelopmentHeaderAuthenticationHandler.RolesHeader, roles);
        }

        return request;
    }
}
