using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Fy1Switch.Contracts;

namespace Fy1Switch.Remote;

public sealed class HttpsControlServer(RemoteGateway gateway, DeviceSecurity security) : IAsyncDisposable
{
    private WebApplication? _application;
    private WebApplication? _tailscale;
    public string TailscaleStatus { get; private set; } = "disabled";
    public async Task StartAsync(RemoteSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        if (_application is not null) throw new InvalidOperationException("already_running");
        gateway.PublicEndpoint = settings.PublicEndpoint.TrimEnd('/');
        gateway.TailscaleEndpoint = null;
        TailscaleStatus = "disabled";
        _application = await StartListenerAsync(settings.ListenAddress, settings.Port, cancellationToken);
        if (!settings.UseTailscale || settings.TailscaleListenAddress is not { } address) return;
        try
        {
            var primary = IPAddress.Parse(settings.ListenAddress);
            var secondary = IPAddress.Parse(address);
            if (!primary.Equals(secondary) && !primary.Equals(IPAddress.IPv6Any)
                && !(primary.Equals(IPAddress.Any) && secondary.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                _tailscale = await StartListenerAsync(address, settings.Port, cancellationToken);
            gateway.TailscaleEndpoint = settings.TailscaleEndpoint;
            TailscaleStatus = "listening";
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is System.IO.IOException or System.Net.Sockets.SocketException)
        {
            TailscaleStatus = "unavailable";
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }
    private async Task<WebApplication> StartListenerAsync(string address, int port, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 131072;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            options.Listen(IPAddress.Parse(address), port, listener => listener.UseHttps(security.Certificate));
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddFixedWindowLimiter("control", limiter => { limiter.PermitLimit = 90; limiter.Window = TimeSpan.FromMinutes(1); limiter.QueueLimit = 0; });
            options.AddFixedWindowLimiter("pair", limiter => { limiter.PermitLimit = 10; limiter.Window = TimeSpan.FromMinutes(1); limiter.QueueLimit = 0; });
        });
        var app = builder.Build();
        app.Use(async (context, next) => { context.Response.Headers.CacheControl = "no-store"; await next(context); });
        app.UseRateLimiter();
        app.MapPost("/v1/pair", async context =>
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var request = await ReadBoundedAsync<PairRequest>(context.Request, deadline.Token);
                var reply = request is null ? null : gateway.Pair(request);
                context.Response.StatusCode = reply is null ? 401 : 200;
                await context.Response.WriteAsJsonAsync<object>(reply is null ? new { errorCode = "pairing_rejected" } : reply, WireJson.Options, deadline.Token);
            }
            catch (Exception exception) when (exception is JsonException or BadHttpRequestException) { context.Response.StatusCode = 400; }
            catch (OperationCanceledException) { context.Response.StatusCode = 408; }
        }).RequireRateLimiting("pair");
        app.MapPost("/v1/relay", async context =>
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                var authorization = context.Request.Headers.Authorization.ToString();
                var token = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : null;
                if (!security.Authenticate(token)) { context.Response.StatusCode = 401; return; }
                var request = await ReadBoundedAsync<RelayAuthorizeRequest>(context.Request, deadline.Token);
                if (request is null) { context.Response.StatusCode = 400; return; }
                var result = await gateway.RelayConfigurationAsync(token, request, deadline.Token);
                context.Response.StatusCode = result.StatusCode;
                if (result.Reply is not null) await context.Response.WriteAsJsonAsync(result.Reply, WireJson.Options, deadline.Token);
            }
            catch (Exception exception) when (exception is JsonException or BadHttpRequestException) { context.Response.StatusCode = 400; }
            catch (OperationCanceledException) { context.Response.StatusCode = 504; }
        }).RequireRateLimiting("control");
        app.MapPost("/v1/control", async context =>
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                deadline.CancelAfter(TimeSpan.FromSeconds(120));
                var authorization = context.Request.Headers.Authorization.ToString();
                var token = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : null;
                if (!security.Authenticate(token)) { context.Response.StatusCode = 401; return; }
                var request = await ReadBoundedAsync<RemoteCommand>(context.Request, deadline.Token);
                if (request is null) { context.Response.StatusCode = 400; return; }
                var reply = await gateway.ControlAsync(token, request, deadline.Token);
                context.Response.StatusCode = reply.StatusCode;
                await context.Response.WriteAsJsonAsync(reply.Reply, WireJson.Options, deadline.Token);
            }
            catch (Exception exception) when (exception is JsonException or BadHttpRequestException) { context.Response.StatusCode = 400; }
            catch (OperationCanceledException) { context.Response.StatusCode = 504; }
        }).RequireRateLimiting("control");
        try { await app.StartAsync(cancellationToken); return app; }
        catch { await app.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        gateway.TailscaleEndpoint = null;
        TailscaleStatus = "disabled";
        var tailscale = _tailscale;
        _tailscale = null;
        try { if (tailscale is not null) await StopAsync(tailscale); }
        finally
        {
            if (_application is { } app)
            {
                _application = null;
                await StopAsync(app);
            }
        }
    }
    private static async Task StopAsync(WebApplication app)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(timeout.Token); } finally { await app.DisposeAsync(); }
    }
    private static async Task<T?> ReadBoundedAsync<T>(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > 65536) throw new BadHttpRequestException("body_too_large", 413);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await request.Body.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (body.Length + count > 65536) throw new BadHttpRequestException("body_too_large", 413);
            body.Write(buffer, 0, count);
        }
        var bytes = body.ToArray();
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (document.RootElement.EnumerateObject().Any(property => !names.Add(property.Name))) throw new JsonException("duplicate_property");
        }
        return JsonSerializer.Deserialize<T>(bytes, WireJson.Options);
    }
}
