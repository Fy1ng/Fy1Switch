using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fy1Switch.Remote.Tests;

[TestClass]
public sealed class RelayConnectionStatusTests
{
    [DataTestMethod]
    [DataRow(401, "unauthorized")]
    [DataRow(403, "unauthorized")]
    [DataRow(409, "conflict")]
    [DataRow(500, "unavailable")]
    public async Task RejectedHandshakeRetainsItsReasonDuringReconnectBackoff(int statusCode, string expected)
    {
        using var fixture = new RemoteFixture();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            endpoint => endpoint.UseHttps(fixture.Security.Certificate)));
        await using var server = builder.Build();
        server.MapGet("/v1/devices/{deviceId:guid}/connect", () => Results.StatusCode(statusCode));
        await server.StartAsync();
        var endpoint = server.Urls.Single();
        var expectedPin = fixture.Security.CertificatePin;
        await using var connection = new RelayConnection(fixture.Gateway, endpoint, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))
        {
            SocketFactory = () =>
            {
                var socket = new ClientWebSocket();
                socket.Options.Proxy = null;
                socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null) return false;
                    using var cert = new X509Certificate2(certificate);
                    using var key = cert.GetRSAPublicKey();
                    return key is not null && DateTime.UtcNow >= cert.NotBefore.ToUniversalTime() && DateTime.UtcNow <= cert.NotAfter.ToUniversalTime() &&
                        "sha256/" + Convert.ToBase64String(SHA256.HashData(key.ExportSubjectPublicKeyInfo())) == expectedPin;
                };
                return socket;
            }
        };
        connection.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (connection.Status != expected) await Task.Delay(20, deadline.Token);
        await Task.Delay(100);
        Assert.AreEqual(expected, connection.Status, "The error must remain visible instead of being replaced immediately by disconnected.");
    }
}
