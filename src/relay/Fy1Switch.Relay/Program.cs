using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Fy1Switch.Contracts;
using Fy1Switch.Relay;

await RelayApplication.Build(args).RunAsync();

public static class RelayApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5080");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 * RelayRouter.MessageLimit);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.PostConfigure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
            options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.None);
        var settings = RelaySettings.Load(builder.Configuration["FY1SWITCH_RELAY_CONFIG"]);
        builder.Services.AddSingleton(settings);
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton<RelayRegistry>();
        builder.Services.AddSingleton<RelayRouter>();
        builder.Services.AddRelayWebUi(settings);
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddOptions<RateLimiterOptions>().Configure<RelaySettings>((options, settings) =>
        {
            options.RejectionStatusCode = 429;
            var perIp = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown-peer", _ =>
                    new FixedWindowRateLimiterOptions { PermitLimit = settings.RequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            var global = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetFixedWindowLimiter("relay-ingress", _ =>
                    new FixedWindowRateLimiterOptions { PermitLimit = (int)Math.Min((long)Math.Max(settings.DeviceCount, settings.Management is null ? 1 : 32) * settings.RequestsPerMinute, 6000), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(global, perIp);
            options.AddPolicy("bootstrap", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown-peer", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        configure?.Invoke(builder);
        var app = builder.Build();
        _ = app.Services.GetRequiredService<RelaySettings>();
        _ = app.Services.GetRequiredService<RelayRegistry>();
        app.InitializeRelayWebUiAsync().GetAwaiter().GetResult();
        app.UseRateLimiter();
        app.UseRelayWebUi();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(20) });

        app.MapGet("/v1/devices/{deviceId:guid}/connect", async (Guid deviceId, HttpContext context, RelaySettings settings, RelayRegistry registry, RelayRouter router) =>
        {
            if (!Authenticate(context, settings, registry, deviceId, windows: true)) return;
            if (!context.WebSockets.IsWebSocketRequest)
            {
                await WriteError(context, 400, "websocket_required");
                return;
            }
            using var connection = router.Reserve(deviceId);
            if (connection is null)
            {
                await WriteError(context, 409, "device_already_connected");
                return;
            }
            // Revocation can race authentication and reservation; recheck before accepting.
            if (!Authenticate(context, settings, registry, deviceId, windows: true)) return;
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
            await connection.RunAsync(socket, stopping.Token);
        });

        app.MapPost("/v1/devices/{deviceId:guid}/control", async (Guid deviceId, HttpContext context, RelaySettings settings, RelayRegistry registry, RelayRouter router) =>
        {
            if (!Authenticate(context, settings, registry, deviceId, windows: false)) return;
            var token = context.Request.Headers["X-Device-Token"];
            if (token.Count != 1 || string.IsNullOrWhiteSpace(token[0]) || token[0]!.Length > 4096)
            {
                await WriteError(context, 400, "device_token_required");
                return;
            }
            var command = await ReadBody<RemoteCommand>(context);
            if (command is null) return;
            if (command.DeviceId != deviceId || command.Version != 1 || command.RequestId == Guid.Empty ||
                command.Command is not ("status" or "refresh" or "set" or "operation"))
            {
                await WriteError(context, 400, "invalid_command");
                return;
            }
            var now = DateTimeOffset.UtcNow;
            if (command.ExpiresAt <= now)
            {
                await WriteError(context, 504, "deadline_expired");
                return;
            }
            if (command.ExpiresAt > now.AddSeconds(120))
            {
                await WriteError(context, 400, "invalid_deadline");
                return;
            }
            var result = await router.ForwardAsync(deviceId, new RelayEnvelope(command.RequestId, "control", token[0], command), context.RequestAborted);
            await WriteResult(context, result);
        });

        app.MapPost("/v1/devices/{deviceId:guid}/pair", async (Guid deviceId, HttpContext context, RelaySettings settings, RelayRegistry registry, RelayRouter router) =>
        {
            if (!Authenticate(context, settings, registry, deviceId, windows: false)) return;
            var pair = await ReadBody<PairRequest>(context);
            if (pair is null) return;
            if (string.IsNullOrWhiteSpace(pair.Code) || string.IsNullOrWhiteSpace(pair.ClientName) || pair.Code.Length > 128 || pair.ClientName.Length > 256)
            {
                await WriteError(context, 400, "invalid_pair_request");
                return;
            }
            var result = await router.ForwardAsync(deviceId, new RelayEnvelope(Guid.NewGuid(), "pair", null, Pair: pair), context.RequestAborted);
            await WriteResult(context, result);
        });

        app.MapRelayEnrollment();
        return app;
    }

    internal static bool Authenticate(HttpContext context, RelaySettings settings, RelayRegistry registry, Guid deviceId, bool windows)
    {
        var values = context.Request.Headers.Authorization;
        var header = values.Count == 1 ? values[0] : null;
        var token = header is { Length: > 7 and <= 179 } && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : string.Empty;
        if (registry.Contains(deviceId) ? registry.Authenticate(deviceId, token, windows) : settings.Authenticate(deviceId, token, windows)) return true;
        context.Response.StatusCode = 401;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return false;
    }

    internal static async Task<T?> ReadBody<T>(HttpContext context) where T : class
    {
        if (!context.Request.HasJsonContentType())
        {
            await WriteError(context, 415, "json_required");
            return null;
        }
        if (context.Request.ContentLength > RelayRouter.MessageLimit)
        {
            await WriteError(context, 413, "message_too_large");
            return null;
        }
        var buffer = new byte[RelayRouter.MessageLimit + 1];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(length), context.RequestAborted);
                if (read == 0) break;
                length += read;
            }
            if (length > RelayRouter.MessageLimit)
            {
                await WriteError(context, 413, "message_too_large");
                return null;
            }
            return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), RelayJson.Options) ?? throw new JsonException();
        }
        catch (JsonException) { await WriteError(context, 400, "invalid_json"); }
        catch (BadHttpRequestException error) { await WriteError(context, error.StatusCode, "invalid_body"); }
        return null;
    }

    internal static Task WriteError(HttpContext context, int status, string code) => WriteResult(context, RelayResult.Error(status, code));
    internal static async Task WriteResult(HttpContext context, RelayResult result)
    {
        context.Response.StatusCode = result.StatusCode;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(result.Body, RelayJson.Options, cancellationToken: context.RequestAborted);
    }

}

public partial class Program;
