using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Fy1Switch.WolGateway;

if (args.FirstOrDefault() == "admin")
{
    Environment.ExitCode = GatewayAdmin.Run(args.Skip(1).ToArray());
    return;
}
await WolApplication.Build(args).RunAsync();

public static class WolApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5090");
        var urls = builder.Configuration["urls"] ?? "http://127.0.0.1:5090";
        if (urls.Split(';').Any(value => !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http"))
            throw new InvalidOperationException("WOL Gateway must listen on loopback HTTP behind an HTTPS reverse proxy.");
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Leave room for the application to return its stable 8 KiB error envelope.
            options.Limits.MaxRequestBodySize = 16384;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            options.Limits.MaxConcurrentConnections = 64;
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.PostConfigure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
            options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.None);
        var settings = GatewaySettings.Load(builder.Configuration["FY1SWITCH_WOL_CONFIG"]);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<GatewayStore>();
        builder.Services.AddSingleton<GatewayLease>();
        builder.Services.AddSingleton<IWolExecutor, OpenSshExecutor>();
        builder.Services.AddSingleton<WakeService>();
        builder.Services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetFixedWindowLimiter("ingress", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = settings.RequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.OnRejected = async (context, token) =>
            {
                context.HttpContext.Response.StatusCode = 429;
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(new GatewayError(1, "rate_limited"), WolJson.Options, cancellationToken: token);
            };
        });
        configure?.Invoke(builder);
        var app = builder.Build();
        _ = app.Services.GetRequiredService<GatewayLease>();
        app.Services.GetRequiredService<GatewayStore>().RecoverInterruptedOperations();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            try { await next(context); }
            catch (GatewayException error)
            {
                if (context.Response.HasStarted) throw;
                context.Response.StatusCode = error.StatusCode;
                if (error.StatusCode == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
                if (error.StatusCode == 429) context.Response.Headers.RetryAfter = "60";
                await context.Response.WriteAsJsonAsync(new GatewayError(1, error.Code), WolJson.Options, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                if (context.Response.HasStarted) throw;
                // Do not put SQL, configuration paths, tokens, request bodies, or SSH stderr into logs.
                app.Logger.LogError("WOL state storage unavailable.");
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new GatewayError(1, "storage_unavailable"), WolJson.Options, context.RequestAborted);
            }
        });
        app.UseRateLimiter();
        app.MapGet("/healthz", (GatewayStore store) => { store.CheckStorage(); return Results.Json(new { status = "ok", version = 1 }); });
        app.MapPost("/v1/wol/register", async (HttpContext context, GatewayStore store) =>
            Results.Json(store.Register(await ReadBody<RegistrationRequest>(context)), WolJson.Options));
        app.MapGet("/v1/wol/devices", (HttpContext context, GatewayStore store) => Results.Json(store.Devices(Client(context, store)), WolJson.Options));
        app.MapDelete("/v1/wol/clients/me", (HttpContext context, GatewayStore store) =>
        {
            store.Revoke(Client(context, store));
            return Results.NoContent();
        });
        app.MapPost("/v1/wol/devices/{deviceId:guid}/wake", async (Guid deviceId, HttpContext context, GatewayStore store, WakeService service, TimeProvider time) =>
        {
            var client = Client(context, store);
            var request = await ReadBody<WakeRequest>(context);
            WolValidation.Envelope(request.Version, deviceId, request.DeviceId, request.RequestId, request.IssuedAt, request.ExpiresAt, time.GetUtcNow());
            var result = await service.Wake(client, request, app.Lifetime.ApplicationStopping);
            return Results.Json(result, WolJson.Options);
        });
        app.MapGet("/v1/wol/devices/{deviceId:guid}/operations/{operationId:guid}", (Guid deviceId, Guid operationId, HttpContext context, GatewayStore store) =>
            Results.Json(store.Operation(Client(context, store), deviceId, operationId), WolJson.Options));
        app.MapPost("/v1/wol/devices/{deviceId:guid}/check", async (Guid deviceId, HttpContext context, GatewayStore store, WakeService service, TimeProvider time) =>
        {
            var client = Client(context, store);
            var request = await ReadBody<CheckRequest>(context);
            WolValidation.Envelope(request.Version, deviceId, request.DeviceId, request.RequestId, request.IssuedAt, request.ExpiresAt, time.GetUtcNow());
            await service.Check(client, request, context.RequestAborted);
            return Results.Json(new { version = 1, deviceId, status = "ready" }, WolJson.Options);
        });
        return app;
    }

    private static Guid Client(HttpContext context, GatewayStore store)
    {
        var headers = context.Request.Headers.Authorization;
        var value = headers.Count == 1 ? headers[0] : null;
        var token = value is { Length: 50 } && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..] : null;
        var client = store.Authenticate(token);
        store.AdmitClientRequest(client);
        return client;
    }

    private static async Task<T> ReadBody<T>(HttpContext context)
    {
        if (!context.Request.HasJsonContentType()) throw new GatewayException(415, "json_required");
        if (context.Request.ContentLength > 8192) throw new GatewayException(413, "message_too_large");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var buffer = new byte[8193];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var count = await context.Request.Body.ReadAsync(buffer.AsMemory(length), timeout.Token);
                if (count == 0) break;
                length += count;
            }
            if (length > 8192) throw new GatewayException(413, "message_too_large");
            // Reject duplicate fields as well as unknown fields: an ambiguous signed/hashed envelope is not accepted.
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 12 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != document.RootElement.GetPropertyCount())
                throw new GatewayException(400, "invalid_request");
            return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), WolJson.Options) ?? throw new JsonException();
        }
        catch (JsonException) { throw new GatewayException(400, "invalid_request"); }
        catch (BadHttpRequestException error) { throw new GatewayException(error.StatusCode, "invalid_request"); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { throw new GatewayException(408, "request_timeout"); }
    }
}

public partial class Program;
