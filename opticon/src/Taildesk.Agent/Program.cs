using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Taildesk.Agent;
using Taildesk.Shared;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Taildesk Agent requires Windows.");
    return;
}

var configPath = args.FirstOrDefault(argument => argument.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))?[9..]
                 ?? AppPaths.AgentConfigFile;
var configStore = new JsonFileStore<AgentConfig>(configPath);
var config = await configStore.LoadAsync();
if (string.IsNullOrWhiteSpace(config.AgentTokenHash) || config.SharedRoots.Count == 0)
{
    Console.Error.WriteLine($"Taildesk Agent is not configured. Expected {configPath}.");
    return;
}

if (!RemoteAdministrationProtocol.IsTailscaleIpv4(config.BindAddress)
    || !IPAddress.TryParse(config.BindAddress, out var configuredAddress))
{
    Console.Error.WriteLine("Taildesk Agent requires a canonical IPv4 address in Tailscale's 100.64.0.0/10 range.");
    return;
}

for (var attempt = 0; attempt < 120 && !AddressIsAssigned(configuredAddress); attempt++)
{
    if (attempt == 0) Console.WriteLine("Waiting for the Tailscale interface…");
    await Task.Delay(TimeSpan.FromSeconds(2.5));
}
if (!AddressIsAssigned(configuredAddress))
{
    Console.Error.WriteLine("The configured Tailscale address did not become available within five minutes.");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(configuredAddress, config.ApiPort);
    options.Limits.MaxRequestBodySize = config.MaxUploadBytes;
});
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 20L * 1024 * 1024 * 1024);
builder.Services.AddSingleton(configStore);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<AgentState>();
builder.Services.AddSingleton<TailscaleCli>();
builder.Services.AddSingleton<AgentRuntime>();
builder.Services.AddSingleton(new PathGuard(config.SharedRoots));
builder.Services.AddSingleton<FileOperations>();
builder.Services.AddSingleton<UpdateManager>();
builder.Services.AddSingleton<SshAccessManager>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SshAccessManager>());
builder.Services.AddHttpClient(nameof(EnrollmentWorker), client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHostedService<EnrollmentWorker>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();
var updateHealthToken =
    await app.Services.GetRequiredService<UpdateManager>().EnsureHealthTokenAsync();

app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (string.Equals(context.Request.Path.Value, "/internal/update-health", StringComparison.Ordinal))
    {
        var remote = context.Connection.RemoteIpAddress;
        var healthHeader = context.Request.Headers["X-Opticon-Update-Health"].ToString();
        if (remote?.AddressFamily != AddressFamily.InterNetwork || !remote.Equals(configuredAddress)
            || !SecurityHelpers.FixedTimeEquals(healthHeader, updateHealthToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next();
        return;
    }

    if (string.Equals(context.Request.Path.Value, "/api/v1/media", StringComparison.Ordinal))
    {
        await next();
        return;
    }

    var header = context.Request.Headers.Authorization.ToString();
    var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : string.Empty;
    if (!SecurityHelpers.FixedTimeEquals(SecurityHelpers.HashToken(token), config.AgentTokenHash))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "A valid Taildesk agent token is required." });
        return;
    }

    await next();
});

app.Use(async (context, next) =>
{
    var sensitive = context.Request.Path.StartsWithSegments("/api/v1/security")
                    || context.Request.Path.StartsWithSegments("/api/v1/ssh")
                    || context.Request.Path.StartsWithSegments("/api/v1/update")
                    || string.Equals(context.Request.Path.Value, "/api/v1/actions/exit-node", StringComparison.Ordinal);
    if (sensitive)
    {
        var remote = context.Connection.RemoteIpAddress;
        var coordinatorHost = Uri.TryCreate(config.CoordinatorUrl, UriKind.Absolute, out var coordinator)
            && RemoteAdministrationProtocol.IsTailscaleIpv4(coordinator.Host)
            && IPAddress.TryParse(coordinator.Host, out var coordinatorAddress)
            ? coordinatorAddress
            : null;
        if (remote?.AddressFamily != AddressFamily.InterNetwork
            || coordinatorHost is null
            || !remote.Equals(coordinatorHost))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "This operation is restricted to the primary command center." });
            return;
        }
    }

    await next();
});

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (FileNotFoundException exception)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { error = exception.Message });
    }
    catch (UnauthorizedAccessException exception)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = exception.Message });
    }
    catch (IOException exception)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new { error = exception.Message });
    }
    catch (InvalidDataException exception)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = exception.Message });
    }
    catch (ArgumentException exception)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new { error = exception.Message });
    }
});

app.MapGet("/healthz", () => Results.Ok(new { service = "taildesk-agent", status = "ok" }));
app.MapGet("/internal/update-health", (UpdateManager updates) => Results.Ok(updates.GetInternalHealth()));
app.MapGet("/api/v1/status", async (AgentRuntime runtime, CancellationToken cancellationToken) =>
    Results.Ok(await runtime.GetStatusAsync(cancellationToken)));
app.MapGet("/api/v1/roots", (FileOperations files) => Results.Ok(files.GetRoots()));
app.MapGet("/api/v1/files", (string root, string? path, FileOperations files) => Results.Ok(files.List(root, path)));
app.MapGet("/api/v1/files/download", (string root, string path, FileOperations files) =>
{
    var stream = files.OpenRead(root, path);
    return Results.File(stream, ContentType(path), Path.GetFileName(path), enableRangeProcessing: true);
});
app.MapPost("/api/v1/files/upload", async (
    HttpRequest request,
    string root,
    string path,
    string fileName,
    bool? overwrite,
    FileOperations files,
    CancellationToken cancellationToken) =>
{
    if (request.ContentLength is not long length || length <= 0) return Results.StatusCode(StatusCodes.Status411LengthRequired);
    if (length > config.MaxUploadBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    var uploaded = await files.UploadAsync(root, path, fileName, request.Body, length, overwrite == true, cancellationToken);
    return Results.Ok(new { relativePath = uploaded });
});
app.MapPost("/api/v1/files/mkdir", (CreateDirectoryRequest request, FileOperations files) =>
{
    files.CreateDirectory(request.Root, request.RelativePath);
    return Results.NoContent();
});
app.MapDelete("/api/v1/files", (string root, string path, bool? recursive, FileOperations files) =>
{
    files.Delete(root, path, recursive == true);
    return Results.NoContent();
});
app.MapPost("/api/v1/media-link", (MediaLinkRequest request, AgentState state, FileOperations files) =>
{
    _ = files.ResolveFile(request.Root, request.RelativePath);
    var expires = DateTimeOffset.UtcNow.AddMinutes(5);
    var nonce = SecurityHelpers.CreateToken(16);
    var signature = SecurityHelpers.CreateMediaSignature(
        state.Config.MediaSigningKey,
        "GET",
        request.Root,
        request.RelativePath,
        expires.ToUnixTimeSeconds(),
        nonce);
    var relative = $"/api/v1/media?root={Uri.EscapeDataString(request.Root)}&path={Uri.EscapeDataString(request.RelativePath)}&expires={expires.ToUnixTimeSeconds()}&nonce={Uri.EscapeDataString(nonce)}&signature={signature}";
    return Results.Ok(new MediaLinkResponse { RelativeUrl = relative, ExpiresAt = expires });
});
app.MapGet("/api/v1/media", (string root, string path, long expires, string nonce, string signature, AgentState state, FileOperations files) =>
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var expected = SecurityHelpers.CreateMediaSignature(state.Config.MediaSigningKey, "GET", root, path, expires, nonce);
    if (expires < now || expires > now + 15 * 60 || !SecurityHelpers.FixedTimeEquals(signature, expected))
    {
        return Results.Unauthorized();
    }

    var stream = files.OpenRead(root, path);
    return Results.File(stream, ContentType(path), enableRangeProcessing: true);
});
app.MapPost("/api/v1/actions/exit-node", async (ExitNodeRequest request, AgentRuntime runtime, CancellationToken cancellationToken) =>
{
    await runtime.SetExitNodeAdvertisementAsync(request.Enabled, cancellationToken);
    return Results.Ok(new { enabled = request.Enabled });
});
app.MapPost("/api/v1/security/rotate", async (CredentialRotationRequest request, AgentRuntime runtime, CancellationToken cancellationToken) =>
{
    await runtime.RotateCredentialsAsync(request.NewAgentToken, request.NewRustDeskPassword, cancellationToken);
    return Results.NoContent();
});
app.MapPost("/api/v1/security/role", async (RoleChangeRequest request, AgentRuntime runtime, CancellationToken cancellationToken) =>
{
    await runtime.SetRoleAsync(request.Role, cancellationToken);
    return Results.NoContent();
});

app.MapGet("/api/v1/ssh/status", async (SshAccessManager ssh, CancellationToken cancellationToken) =>
    Results.Ok(await ssh.GetStatusAsync(cancellationToken)));
app.MapPost("/api/v1/ssh/access", async (
    SshAccessRequest request,
    HttpContext context,
    SshAccessManager ssh,
    CancellationToken cancellationToken) =>
{
    var caller = context.Connection.RemoteIpAddress
                 ?? throw new UnauthorizedAccessException("The SSH caller address is unavailable.");
    var lifetime = request.ExpiresAt - DateTimeOffset.UtcNow;
    if (lifetime <= TimeSpan.Zero || lifetime > RemoteAdministrationProtocol.MaximumSshSession)
        throw new ArgumentOutOfRangeException(nameof(request.ExpiresAt), "The SSH lease expiry is outside the allowed window.");
    var grant = await ssh.ProvisionAsync(caller, request.PublicKey, lifetime, cancellationToken);
    return Results.Ok(new SshAccessResponse
    {
        SessionId = grant.SessionId,
        UserName = grant.UserName,
        Port = grant.Port,
        Host = grant.Host,
        ExpiresAt = grant.ExpiresAt,
        HostPublicKey = grant.HostPublicKey,
        SystemRoot = grant.SystemRoot
    });
});
app.MapPost("/api/v1/ssh/revoke", async (
    SshRevokeRequest request,
    HttpContext context,
    SshAccessManager ssh,
    CancellationToken cancellationToken) =>
{
    var caller = context.Connection.RemoteIpAddress
                 ?? throw new UnauthorizedAccessException("The SSH caller address is unavailable.");
    await ssh.RevokeAsync(caller, request.SessionId, cancellationToken);
    return Results.NoContent();
});

app.MapGet("/api/v1/update/status", (UpdateManager updates) => Results.Ok(
    updates.GetStatus() ?? new UpdateStatusDto
    {
        Phase = UpdatePhase.None,
        CurrentVersion = UpdateManager.CurrentVersion,
        UpdatedAt = DateTimeOffset.UtcNow,
        Message = "No Opticon update is staged."
    }));
app.MapPost("/api/v1/update/prepare", async (OpticonUpdateRequest request, UpdateManager updates, CancellationToken cancellationToken) =>
    Results.Ok(await updates.PrepareAsync(request, cancellationToken)));
app.MapPost("/api/v1/update/activate", async (UpdateOperationRequest request, UpdateManager updates, CancellationToken cancellationToken) =>
    Results.Ok(await updates.ActivateAsync(request.OperationId, cancellationToken)));
app.MapPost("/api/v1/update/commit", async (UpdateOperationRequest request, UpdateManager updates, CancellationToken cancellationToken) =>
    Results.Ok(await updates.RequestCommitAsync(request.OperationId, cancellationToken)));

await app.RunAsync();

static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".mp4" => "video/mp4",
    ".mkv" => "video/x-matroska",
    ".webm" => "video/webm",
    ".mp3" => "audio/mpeg",
    ".wav" => "audio/wav",
    ".flac" => "audio/flac",
    ".jpg" or ".jpeg" => "image/jpeg",
    ".png" => "image/png",
    ".gif" => "image/gif",
    ".pdf" => "application/pdf",
    ".txt" or ".log" => "text/plain",
    _ => "application/octet-stream"
};

static bool AddressIsAssigned(IPAddress address) => NetworkInterface.GetAllNetworkInterfaces()
    .SelectMany(network => network.GetIPProperties().UnicastAddresses)
    .Any(item => item.Address.Equals(address));
