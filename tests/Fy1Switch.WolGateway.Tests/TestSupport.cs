using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fy1Switch.WolGateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Fy1Switch.WolGateway.Tests;

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan amount) => now += amount;
}
internal sealed class FakeExecutor : IWolExecutor
{
    public int WakeCount;
    public int CheckCount;
    public SshExecution WakeResult = SshExecution.Sent;
    public SshExecution CheckResult = SshExecution.Ready;
    public Func<CancellationToken, Task>? BeforeReturn;
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<SshExecution> ExecuteAsync(WolDevice device, bool wake, CancellationToken cancellationToken)
    {
        if (wake) Interlocked.Increment(ref WakeCount); else Interlocked.Increment(ref CheckCount);
        Entered.TrySetResult();
        if (BeforeReturn is not null) await BeforeReturn(cancellationToken);
        return wake ? WakeResult : CheckResult;
    }
}
internal sealed class TestStore : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(AppContext.BaseDirectory, "TestResults", Guid.NewGuid().ToString("N"));
    public Guid DeviceId { get; } = Guid.NewGuid();
    public Guid OtherDeviceId { get; } = Guid.NewGuid();
    public TestClock Clock { get; } = new();
    public GatewaySettings Settings { get; }
    public GatewayStore Store { get; }
    public TestStore(int clientLimit = 60)
    {
        Settings = new("https://wol.example.test", DirectoryPath,
            new("10.88.0.2", "root", Path.Combine(DirectoryPath, "key"), Path.Combine(Path.GetPathRoot(DirectoryPath)!, "wol-test-known-hosts"), "10.88.0.1", "wg0", Executable: Path.Combine(DirectoryPath, "ssh")),
            [new(DeviceId, "PC One", "02:11:22:33:44:55", "br-lan"), new(OtherDeviceId, "PC Two", "02:11:22:33:44:66", "br-lan")], ClientRequestsPerMinute: clientLimit);
        Settings.Validate();
        Store = new GatewayStore(Settings, Clock);
    }
    public RegistrationReply Register(params Guid[] ids)
    {
        var code = Store.GenerateCode(ids.Length == 0 ? [DeviceId] : ids);
        return Store.Register(new(1, code.Code, "Test phone"));
    }
    public WakeRequest Wake(Guid? deviceId = null, Guid? operationId = null) =>
        new(1, deviceId ?? DeviceId, Guid.NewGuid(), operationId ?? Guid.NewGuid(), Clock.GetUtcNow(), Clock.GetUtcNow().AddSeconds(60));
    public CheckRequest Check() => new(1, DeviceId, Guid.NewGuid(), Clock.GetUtcNow(), Clock.GetUtcNow().AddSeconds(60));
    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
    }
}
internal sealed class GatewayFixture : IAsyncDisposable
{
    public TestStore Data { get; } = new();
    public FakeExecutor Executor { get; } = new();
    public HttpClient Client { get; } = new() { Timeout = TimeSpan.FromSeconds(10) };
    public WebApplication App { get; private set; } = null!;
    public static async Task<GatewayFixture> Start()
    {
        var fixture = new GatewayFixture();
        var path = Path.Combine(fixture.Data.DirectoryPath, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(fixture.Data.Settings, WolJson.Options));
        fixture.App = WolApplication.Build(["--FY1SWITCH_WOL_CONFIG", path, "--urls", "http://127.0.0.1:0"], builder =>
        {
            builder.Logging.ClearProviders();
            builder.Services.RemoveAll<TimeProvider>();
            builder.Services.AddSingleton<TimeProvider>(fixture.Data.Clock);
            builder.Services.RemoveAll<IWolExecutor>();
            builder.Services.AddSingleton<IWolExecutor>(fixture.Executor);
        });
        await fixture.App.StartAsync();
        var uri = new Uri(fixture.App.Urls.Single());
        Assert.IsTrue(IPAddress.IsLoopback(IPAddress.Parse(uri.Host)));
        fixture.Client.BaseAddress = uri;
        return fixture;
    }
    public Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, string? token = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, WolJson.Options), Encoding.UTF8, "application/json");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return SendAndDispose(request);
    }
    private async Task<HttpResponseMessage> SendAndDispose(HttpRequestMessage request)
    { using (request) return await Client.SendAsync(request); }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (App is not null) { await App.StopAsync(); await App.DisposeAsync(); }
        Data.Dispose();
    }
    public static async Task<T> Body<T>(HttpResponseMessage response) => JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(), WolJson.Options)!;
}
