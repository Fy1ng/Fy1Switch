using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Relay;

namespace RemoteDisplaySwitch.Relay.Tests;

internal sealed class RelayFixture : IAsyncDisposable
{
    private readonly List<ClientWebSocket> sockets = new();
    private readonly string configDirectory;
    private WebApplication? app;
    public Guid DeviceId { get; } = Guid.NewGuid();
    public Guid OtherDeviceId { get; } = Guid.NewGuid();
    public string WindowsToken { get; } = RandomToken();
    public string ControllerToken { get; } = RandomToken();
    public string OtherWindowsToken { get; } = RandomToken();
    public string OtherControllerToken { get; } = RandomToken();
    public string DeviceToken { get; } = RandomToken();
    public HttpClient Client { get; } = new() { Timeout = TimeSpan.FromSeconds(15) };
    public Uri BaseUri { get; private set; } = null!;
    public string ConfigPath => Path.Combine(configDirectory, "config.json");

    private RelayFixture()
    {
        configDirectory = Path.Combine(AppContext.BaseDirectory, "TestResults", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);
    }

    public static string RandomToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static async Task<RelayFixture> StartAsync(double timeout = 5, int rateLimit = 120)
    {
        var fixture = new RelayFixture();
        try
        {
            await File.WriteAllTextAsync(fixture.ConfigPath, JsonSerializer.Serialize(new
            {
                devices = new[]
                {
                    new { deviceId = fixture.DeviceId, windowsToken = fixture.WindowsToken, controllerToken = fixture.ControllerToken },
                    new { deviceId = fixture.OtherDeviceId, windowsToken = fixture.OtherWindowsToken, controllerToken = fixture.OtherControllerToken }
                },
                requestTimeoutSeconds = timeout,
                requestsPerMinute = rateLimit
            }));
            fixture.app = RelayApplication.Build(["--RDS_RELAY_CONFIG", fixture.ConfigPath, "--urls", "http://127.0.0.1:0", "--environment", "Production"]);
            await fixture.app.StartAsync();
            fixture.BaseUri = new Uri(fixture.app.Urls.Single());
            if (!IPAddress.IsLoopback(IPAddress.Parse(fixture.BaseUri.Host))) throw new InvalidOperationException("Fixture must remain loopback.");
            fixture.Client.BaseAddress = fixture.BaseUri;
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public RemoteCommand Command(string command = "status", double lifetime = 30) =>
        new(Guid.NewGuid(), DeviceId, command, DateTimeOffset.UtcNow.AddSeconds(lifetime));

    public async Task<HttpResponseMessage> PostAsync(string endpoint, object body, string? token = null, Guid? routeId = null, bool includeDeviceToken = true, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/devices/{routeId ?? DeviceId}/{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? ControllerToken);
        if (includeDeviceToken) request.Headers.Add("X-Device-Token", DeviceToken);
        request.Content = new StringContent(JsonSerializer.Serialize(body, RelayJson.Options), Encoding.UTF8, "application/json");
        return await Client.SendAsync(request, cancellationToken);
    }

    public ClientWebSocket NewSocket(string? token = null)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token ?? WindowsToken}");
        socket.Options.CollectHttpResponseDetails = true;
        sockets.Add(socket);
        return socket;
    }

    public async Task<ClientWebSocket> ConnectAsync(string? token = null, Guid? deviceId = null)
    {
        var socket = NewSocket(token);
        await ConnectAsync(socket, deviceId);
        return socket;
    }

    public Task ConnectAsync(ClientWebSocket socket, Guid? deviceId = null) =>
        socket.ConnectAsync(new UriBuilder(BaseUri) { Scheme = "ws", Path = $"/v1/devices/{deviceId ?? DeviceId}/connect" }.Uri, CancellationToken.None);

    public static async Task<byte[]> ReceiveBytesAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = new MemoryStream();
        var buffer = new byte[65536];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            if (received.MessageType != WebSocketMessageType.Text) throw new InvalidOperationException("Expected text frame.");
            stream.Write(buffer, 0, received.Count);
        } while (!received.EndOfMessage);
        return stream.ToArray();
    }

    public static async Task<RelayEnvelope> ReceiveAsync(WebSocket socket) =>
        JsonSerializer.Deserialize<RelayEnvelope>(await ReceiveBytesAsync(socket), RelayJson.Options)!;

    public static Task ReplyAsync(WebSocket socket, RelayResponse response) =>
        socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(response, RelayJson.Options)), WebSocketMessageType.Text, true, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        foreach (var socket in sockets) { socket.Abort(); socket.Dispose(); }
        Client.Dispose();
        if (app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await app.StopAsync(timeout.Token);
            await app.DisposeAsync();
        }
        File.Delete(ConfigPath);
        Directory.Delete(configDirectory);
    }
}
