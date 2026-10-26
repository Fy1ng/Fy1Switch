using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fy1Switch.Relay;

namespace Fy1Switch.Relay.Tests;

[TestClass]
public sealed class ConfigurationTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("relative/config.json")]
    public void ConfigurationRequiresAbsoluteFilePath(string? path) =>
        Assert.ThrowsExactly<InvalidOperationException>(() => RelaySettings.Load(path));

    [TestMethod]
    [DataRow("empty")]
    [DataRow("weak")]
    [DataRow("placeholder")]
    [DataRow("duplicate-id")]
    [DataRow("duplicate-token")]
    [DataRow("empty-id")]
    [DataRow("long-timeout")]
    [DataRow("null-device")]
    [DataRow("malformed")]
    public void InvalidConfigurationFailsClosedWithoutEchoingTokens(string invalid)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"config-{Guid.NewGuid():N}.json");
        var token = RelayFixture.RandomToken();
        var first = new { deviceId = invalid == "empty-id" ? Guid.Empty : Guid.NewGuid(), windowsToken = invalid == "weak" ? "short-secret" : invalid == "placeholder" ? "REPLACE_WITH_RANDOM_BASE64" : token, controllerToken = invalid == "duplicate-token" ? token : RelayFixture.RandomToken() };
        object?[] devices = invalid switch
        {
            "empty" => [],
            "duplicate-id" => [first, first],
            "null-device" => [null],
            _ => [first]
        };
        try
        {
            File.WriteAllText(path, invalid == "malformed" ? "{\"windowsToken\":\"" + token : JsonSerializer.Serialize(new { devices, requestTimeoutSeconds = invalid == "long-timeout" ? 101 : 30 }));
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => RelaySettings.Load(path));
            Assert.IsFalse(error.ToString().Contains(token, StringComparison.Ordinal));
            Assert.IsFalse(error.ToString().Contains("short-secret", StringComparison.Ordinal));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    [DataRow("http://relay.example.test", false, false)]
    [DataRow("https://relay.example.test/", false, false)]
    [DataRow("https://relay.example.test/management", false, false)]
    [DataRow("https://relay.example.test?key=placeholder", false, false)]
    [DataRow("https://user:password@relay.example.test", false, false)]
    [DataRow("https://127.0.0.1", false, false)]
    [DataRow("https://relay.example.test", true, false)]
    [DataRow("https://relay.example.test", false, true)]
    public void ManagementRequiresFixedHttpsOriginAndAbsolutePrivatePaths(string publicUrl, bool relativeData, bool relativePassword)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                management = new { publicUrl, dataDirectory = relativeData ? "data" : directory, adminPasswordFile = relativePassword ? "secret.txt" : Path.Combine(directory, "secret.txt") }
            }));
            Assert.ThrowsExactly<InvalidOperationException>(() => RelaySettings.Load(path));
        }
        finally { File.Delete(path); }
    }
}
