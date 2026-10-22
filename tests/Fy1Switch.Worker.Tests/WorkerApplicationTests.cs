using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fy1Switch.Contracts;
using Fy1Switch.Worker;

namespace Fy1Switch.Worker.Tests;

[TestClass]
public sealed class WorkerApplicationTests
{
    private static string[] Arguments(string pipe, string directory, int? parentPid = null) =>
        ["--pipe", pipe, "--parent-pid", (parentPid ?? Environment.ProcessId).ToString(), "--data-dir", directory, "--simulate"];

    private static NamedPipeServerStream CreateServer(string name) => new(name, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    [TestMethod]
    public async Task InvalidArgumentsNeverCreateAnyProvider()
    {
        using var directory = new TestDirectory();
        var valid = Arguments("fixture", directory.Path);
        var invalid = new string[][]
        {
            [], ["--simulate"], ["--pipe"], [.. valid, "--unknown"], [.. valid, "--simulate"],
            [.. valid, "--pipe", "again"],
            Arguments("../remote", directory.Path), Arguments(@"\\server\pipe\name", directory.Path),
            Arguments("", directory.Path), Arguments(new string('x', 129), directory.Path),
            Arguments("fixture", directory.Path, 0), Arguments("fixture", directory.Path, -1),
            Arguments("fixture", "relative"), Arguments("fixture", @"\\server\share"),
            Arguments("fixture", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "PowerToys", "PowerDisplay")),
        };
        foreach (var args in invalid)
        {
            Assert.IsFalse(WorkerOptions.TryParse(args, out _));
            Assert.AreEqual(2, await WorkerApplication.RunAsync(args, _ => throw new AssertFailedException("Provider created for invalid arguments")));
        }

        Assert.IsTrue(WorkerOptions.TryParse(valid, out var options));
        Assert.IsTrue(options!.Simulate);
        Assert.IsTrue(WorkerOptions.TryParse(valid[..^1], out options));
        Assert.IsFalse(options!.Simulate);
    }

    [TestMethod]
    public async Task RealNamedPipeAuthenticatesServerPidAndExitsOnShutdown()
    {
        using var directory = new TestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = "fy1switch-worker-test-" + Guid.NewGuid().ToString("N");
        using var server = CreateServer(name);
        var provider = new FakeProvider();
        var worker = WorkerApplication.RunAsync(Arguments(name, directory.Path), _ => provider);
        await server.WaitForConnectionAsync(deadline.Token);
        using var reader = new StreamReader(server, new UTF8Encoding(false, true), leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var request = ProtocolFixture.Request("discover");
        await writer.WriteLineAsync(WireJson.Serialize(request));
        var reply = WireJson.Parse<WorkerReply>((await WireJson.ReadLineAsync(reader, deadline.Token))!);
        Assert.AreEqual(request.RequestId, reply.RequestId);
        Assert.IsTrue(reply.Success);
        await writer.WriteLineAsync(WireJson.Serialize(ProtocolFixture.Request("shutdown")));
        Assert.IsTrue(WireJson.Parse<WorkerReply>((await WireJson.ReadLineAsync(reader, deadline.Token))!).Success);
        Assert.AreEqual(0, await worker.WaitAsync(deadline.Token));
        Assert.IsTrue(provider.Disposed);
        Assert.AreEqual(1, provider.Calls);
    }

    [TestMethod]
    public async Task MismatchedServerPidExitsBeforeCreatingProvider()
    {
        using var directory = new TestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = "fy1switch-worker-test-" + Guid.NewGuid().ToString("N");
        using var server = CreateServer(name);
        var worker = WorkerApplication.RunAsync(Arguments(name, directory.Path, int.MaxValue), _ => throw new AssertFailedException("Unauthenticated provider created"));
        await server.WaitForConnectionAsync(deadline.Token);
        Assert.AreEqual(3, await worker.WaitAsync(deadline.Token));
    }

    [TestMethod]
    public async Task PipeEofExitsAndDisposesProviderWithoutDispatch()
    {
        using var directory = new TestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = "fy1switch-worker-test-" + Guid.NewGuid().ToString("N");
        using var server = CreateServer(name);
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider();
        var worker = WorkerApplication.RunAsync(Arguments(name, directory.Path), _ => { created.SetResult(); return provider; });
        await server.WaitForConnectionAsync(deadline.Token);
        await created.Task.WaitAsync(deadline.Token);
        server.Dispose();
        Assert.AreEqual(0, await worker.WaitAsync(deadline.Token));
        Assert.IsTrue(provider.Disposed);
        Assert.AreEqual(0, provider.Calls);
    }

    [TestMethod]
    public async Task SeparateSimulationProcessUsesUtf8NdjsonAndCleanShutdown()
    {
        using var directory = new TestDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var name = "fy1switch-worker-test-" + Guid.NewGuid().ToString("N");
        using var server = CreateServer(name);
        var assemblyPath = typeof(WorkerApplication).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(assemblyPath);
        foreach (var argument in Arguments(name, Path.Combine(directory.Path, "simulation-data"))) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new AssertFailedException("Could not start simulation worker");
        var standardError = child.StandardError.ReadToEndAsync(deadline.Token);
        var standardOutput = child.StandardOutput.ReadToEndAsync(deadline.Token);
        try
        {
            await server.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(server, new UTF8Encoding(false, true), leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            foreach (var request in new[]
            {
                ProtocolFixture.Request("discover"), ProtocolFixture.Request("set", PowerIntent.Off),
                ProtocolFixture.Request("read"), ProtocolFixture.Request("shutdown"),
            })
            {
                await writer.WriteLineAsync(WireJson.Serialize(request));
                var reply = WireJson.Parse<WorkerReply>((await WireJson.ReadLineAsync(reader, deadline.Token))!);
                Assert.AreEqual(request.RequestId, reply.RequestId);
                Assert.IsTrue(reply.Success);
                if (request.Command == "discover") Assert.AreEqual("模拟显示器", reply.Discovery!.Candidates.Single().Name);
                if (request.Command is "read" or "set")
                {
                    Assert.AreEqual(DisplayPower.Off, reply.Observation!.State);
                    Assert.AreEqual("simulation", reply.Observation.Source);
                }
            }

            await child.WaitForExitAsync(deadline.Token);
            Assert.AreEqual(0, child.ExitCode);
            Assert.AreEqual("", await standardOutput);
            Assert.AreEqual("", await standardError);
            Assert.IsFalse(Directory.Exists(Path.Combine(directory.Path, "simulation-data")));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }
    }
}
