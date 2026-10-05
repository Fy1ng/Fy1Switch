using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Contracts;

namespace RemoteDisplaySwitch.Remote.Tests;

[TestClass]
public class HttpAmbiguityTests
{
    [TestMethod]
    [DataRow("command")]
    [DataRow("Command")]
    public async Task DuplicateCommandFieldsAreRejectedBeforeExecution(string duplicate)
    {
        await using var fixture = await HttpsFixture.StartAsync();
        var phone = await fixture.PairAsync();
        var json = WireJson.Serialize(fixture.Remote.Command("status"));
        json = json[..^1] + $",\"{duplicate}\":\"set\"}}";
        using var response = await fixture.SendRawAsync("/v1/control", json, phone.Token);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(0, fixture.Remote.Controller.Calls.Length);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore == true);
    }
}
