using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RemoteDisplaySwitch.Contracts;
using RemoteDisplaySwitch.Remote;

[assembly: DoNotParallelize]

namespace RemoteDisplaySwitch.Remote.Tests;

internal sealed class TestDirectory : IDisposable
{
    private readonly string temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
    private readonly string directoryName = $"RemoteDisplaySwitch.Remote.Tests-{Guid.NewGuid():N}";
    public string Path { get; }

    public TestDirectory()
    {
        Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(temporaryRoot, directoryName));
        if (Directory.Exists(Path)) throw new IOException("Test directory already exists.");
        Directory.CreateDirectory(Path);
    }

    public void RequireOwnedDirectory()
    {
        var expected = System.IO.Path.GetFullPath(System.IO.Path.Combine(temporaryRoot, directoryName));
        if (!string.Equals(System.IO.Path.GetFullPath(Path), expected, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(System.IO.Path.GetDirectoryName(Path), System.IO.Path.TrimEndingDirectorySeparator(temporaryRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing access outside the owned temporary directory.");
        if ((File.GetAttributes(Path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to follow a test directory reparse point.");
    }

    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        RequireOwnedDirectory();
        if (Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories)
            .Any(entry => (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidOperationException("Refusing recursive deletion through a reparse point.");
        Directory.Delete(Path, recursive: true);
    }
}

internal sealed class TestOnlyEncodingProtector(TestDirectory directory) : ISecretProtector
{
    private const string Marker = "TEST-ONLY-NOT-PRODUCTION-ENCRYPTION:";
    public int ProtectCalls { get; private set; }
    public int UnprotectCalls { get; private set; }

    public byte[] Protect(byte[] data)
    {
        directory.RequireOwnedDirectory();
        ProtectCalls++;
        return Encoding.UTF8.GetBytes(Marker + Convert.ToBase64String(data));
    }

    public byte[] Unprotect(byte[] data)
    {
        directory.RequireOwnedDirectory();
        UnprotectCalls++;
        var encoded = Encoding.UTF8.GetString(data);
        if (!encoded.StartsWith(Marker, StringComparison.Ordinal)) throw new InvalidDataException("Not a test-only protected value.");
        return Convert.FromBase64String(encoded[Marker.Length..]);
    }
}

internal sealed class TestClock
{
    public DateTimeOffset Now { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Read() => Now;
    public void Advance(TimeSpan amount) => Now += amount;
}

internal sealed class RemoteFixture : IDisposable
{
    public TestDirectory Directory { get; } = new();
    public TestClock Clock { get; } = new();
    public Guid DeviceId { get; } = Guid.NewGuid();
    public TestOnlyEncodingProtector Protector { get; }
    public DeviceSecurity Security { get; }
    public FakeDesktopController Controller { get; }
    public RemoteGateway Gateway { get; }

    public RemoteFixture()
    {
        Protector = new(Directory);
        Security = new(Directory.Path, Protector, Clock.Read);
        Controller = new(DeviceId, Directory, Clock);
        Gateway = new(Controller, Security, DeviceId, Clock.Read);
    }

    public PairReply Pair()
    {
        var bundle = Security.BeginPairing(DeviceId, "https://127.0.0.1:48152");
        return Gateway.Pair(new(bundle.Code, "Test client")) ?? throw new AssertFailedException("Test pairing was rejected.");
    }

    public RemoteCommand Command(string command = "status") => new(Guid.NewGuid(), DeviceId, command, Clock.Now.AddSeconds(60),
        Controller.Snapshot.Binding?.TargetId, PowerIntent.Off, Guid.NewGuid());

    public void Dispose()
    {
        Security.Dispose();
        Directory.Dispose();
    }
}
