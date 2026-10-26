using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fy1Switch.Contracts;

namespace Fy1Switch.Remote;

public sealed class RelayApiException(HttpStatusCode statusCode) : Exception("relay_request_failed")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class RelayApiClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    public RelayApiClient(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    }
    public async Task<RelayRegistrationReply> RegisterAsync(string endpoint, Guid deviceId, string code, string deviceName, CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty || !DeviceSecurity.IsSixDigitCode(code)) throw new ArgumentException("invalid_relay_code");
        var reply = await SendAsync<RelayRegistrationReply>(endpoint, "/v1/relay/register", HttpMethod.Post, null,
            new RelayRegistrationRequest(deviceId, code, deviceName), cancellationToken);
        if (reply.DeviceId != deviceId || !IsRelayToken(reply.WindowsToken)) throw new InvalidDataException("invalid_relay_response");
        return reply;
    }
    public Task RegisterPairingCodeAsync(string endpoint, Guid deviceId, string token, RelayCode code, CancellationToken cancellationToken = default)
        => SendAsync<object>(endpoint, $"/v1/devices/{deviceId}/pairing-code", HttpMethod.Post, token, code, cancellationToken, noReply: true);
    public Task CancelPairingCodeAsync(string endpoint, Guid deviceId, string token, CancellationToken cancellationToken = default)
        => SendAsync<object>(endpoint, $"/v1/devices/{deviceId}/pairing-code", HttpMethod.Delete, token, null, cancellationToken, noReply: true);
    public Task SynchronizeControllersAsync(string endpoint, Guid deviceId, string token, IReadOnlyList<Guid> clientIds, CancellationToken cancellationToken = default)
        => SendAsync<object>(endpoint, $"/v1/devices/{deviceId}/controllers/synchronize", HttpMethod.Post, token, new RelayClientsRequest(clientIds), cancellationToken, noReply: true);
    public async Task<RelayControllerReply> CreateControllerAsync(string endpoint, Guid deviceId, string token, Guid clientId, CancellationToken cancellationToken = default)
    {
        var reply = await SendAsync<RelayControllerReply>(endpoint, $"/v1/devices/{deviceId}/controllers", HttpMethod.Post, token,
            new RelayAuthorizeRequest(clientId), cancellationToken);
        if (reply.DeviceId != deviceId || reply.ClientId != clientId || !IsRelayToken(reply.ControllerToken)) throw new InvalidDataException("invalid_relay_response");
        return reply;
    }
    private async Task<T> SendAsync<T>(string endpoint, string path, HttpMethod method, string? token, object? payload,
        CancellationToken cancellationToken, bool noReply = false)
    {
        var origin = RemoteSettings.RequireUrl(endpoint, "https");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(method, new Uri(origin, path));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (payload is not null) request.Content = new StringContent(WireJson.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new RelayApiException(response.StatusCode);
        if (noReply) return default!;
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, deadline.Token)) != 0)
        {
            if (body.Length + count > 65536) throw new InvalidDataException("relay_response_too_large");
            body.Write(buffer, 0, count);
        }
        try { return JsonSerializer.Deserialize<T>(body.ToArray(), WireJson.Options) ?? throw new InvalidDataException("invalid_relay_response"); }
        catch (JsonException exception) { throw new InvalidDataException("invalid_relay_response", exception); }
    }
    private static bool IsRelayToken(string? token)
    {
        if (token is null || token.Length > 172) return false;
        Span<byte> bytes = stackalloc byte[128];
        return Convert.TryFromBase64String(token, bytes, out var count) && count >= 32 && Convert.ToBase64String(bytes[..count]) == token;
    }
    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
