using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Remote;

namespace RemoteDisplaySwitch.Remote.Tests;

[TestClass]
public sealed class TlsIdentityTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeviceCertificateCanAuthenticateAsTlsServer(bool reload)
    {
        using var fixture = new RemoteFixture();
        using var reloaded = reload ? new DeviceSecurity(fixture.Directory.Path, fixture.Protector, fixture.Clock.Read) : null;
        var security = reloaded ?? fixture.Security;
        Assert.IsTrue(security.Certificate.HasPrivateKey);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new PinnedHttpClient(new Uri($"https://127.0.0.1:{port}"), security.CertificatePin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = client.Client.GetAsync("/", timeout.Token);
        try
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = new SslStream(connection.GetStream(), leaveInnerStreamOpen: false);
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = security.Certificate }, timeout.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
            using var response = await request;
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(client.AcceptedCertificates > 0);
        }
        finally
        {
            timeout.Cancel();
            try { using var response = await request; }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { }
        }
    }
}
