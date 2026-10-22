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
}
