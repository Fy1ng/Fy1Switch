using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace Fy1Switch.WolGateway;

public sealed record SshExecution(string Status, string? ErrorCode = null)
{
    public static SshExecution Sent { get; } = new("packet_sent");
    public static SshExecution Ready { get; } = new("ready");
}
public interface IWolExecutor
{
    Task<SshExecution> ExecuteAsync(WolDevice device, bool wake, CancellationToken cancellationToken);
}

public sealed class OpenSshExecutor(GatewaySettings settings) : IWolExecutor
{
    public async Task<SshExecution> ExecuteAsync(WolDevice device, bool wake, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) return new("failed", "unsupported_platform");
        if (!TunnelPresent(settings.Ssh)) return new("failed", "tunnel_unavailable");
        if (!File.Exists(settings.Ssh.IdentityFile) || !File.Exists(settings.Ssh.KnownHostsFile) || !File.Exists(settings.Ssh.Executable))
            return new("failed", "ssh_configuration_invalid");
        try
        {
            var mode = File.GetUnixFileMode(settings.Ssh.IdentityFile);
            var hostMode = File.GetUnixFileMode(settings.Ssh.KnownHostsFile);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0 ||
                (hostMode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                return new("failed", "ssh_configuration_invalid");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new("failed", "ssh_configuration_invalid"); }
        using var process = new Process { StartInfo = BuildStartInfo(settings.Ssh, device, wake) };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.Ssh.ExecutionTimeoutSeconds));
        var started = false;
        try
        {
            if (!process.Start()) return new("failed", "ssh_failed");
            started = true;
            // Do not allow an unexpected banner/stderr stream to grow process memory.
            var stdout = ReadBounded(process.StandardOutput, deadline.Token);
            var stderr = ReadBounded(process.StandardError, deadline.Token);
            try
            {
                await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
            }
            catch
            {
                TryKill(process);
                throw;
            }
            return Classify(process.ExitCode, await stdout, await stderr, device, wake);
        }
        catch (OperationCanceledException) { TryKill(process); return new(wake ? "unknown" : "failed", "execution_timeout"); }
        catch (InvalidDataException) { TryKill(process); return new(wake ? "unknown" : "failed", "invalid_ssh_response"); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // A stream failure after launch cannot establish whether the remote command ran.
            TryKill(process);
            return new(started && wake ? "unknown" : "failed", "ssh_failed");
        }
    }

    public static ProcessStartInfo BuildStartInfo(SshSettings ssh, WolDevice device, bool wake)
    {
        var info = new ProcessStartInfo(ssh.Executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var value in new[] { "-F", "/dev/null", "-n", "-T", "-4", "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-o", "IdentityAgent=none",
            "-o", "StrictHostKeyChecking=yes", "-o", "GlobalKnownHostsFile=/dev/null", "-o", "UserKnownHostsFile=" + ssh.KnownHostsFile,
            "-o", "UpdateHostKeys=no", "-o", "VerifyHostKeyDNS=no", "-o", "PasswordAuthentication=no", "-o", "KbdInteractiveAuthentication=no",
            "-o", "PreferredAuthentications=publickey", "-o", "ClearAllForwardings=yes", "-o", "ForwardAgent=no", "-o", "ForwardX11=no",
            "-o", "PermitLocalCommand=no", "-o", "ProxyCommand=none", "-o", "ProxyJump=none", "-o", "ConnectionAttempts=1",
            "-o", "ConnectTimeout=" + ssh.ConnectTimeoutSeconds, "-o", "ServerAliveInterval=3", "-o", "ServerAliveCountMax=1", "-o", "LogLevel=ERROR",
            "-b", ssh.BindAddress, "-B", ssh.WireGuardInterface, "-i", ssh.IdentityFile, "-p", ssh.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-l", ssh.User, "--", ssh.Host, (wake ? "wake " : "check ") + device.DeviceId.ToString("D") + " " + DeviceDigest(device) })
            info.ArgumentList.Add(value);
        if (IPAddress.Parse(ssh.Host).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            info.ArgumentList[4] = "-6";
        info.Environment["LC_ALL"] = "C";
        info.Environment.Remove("SSH_AUTH_SOCK");
        info.Environment.Remove("SSH_ASKPASS");
        return info;
    }

    public static string DeviceDigest(WolDevice device) => Convert.ToHexStringLower(WolValidation.Hash($"{device.DeviceId:D}|{device.MacAddress.ToUpperInvariant()}|{device.Interface}"));
    public static SshExecution Classify(int exitCode, string stdout, string stderr, WolDevice device, bool wake)
    {
        var expected = "FY1SWITCH_WOL_V1 " + (wake ? "packet_sent " : "ready ") + device.DeviceId.ToString("D") + " " + DeviceDigest(device);
        if (exitCode == 0 && stdout.TrimEnd('\r', '\n') == expected) return wake ? SshExecution.Sent : SshExecution.Ready;
        if (exitCode == 0) return new(wake ? "unknown" : "failed", "invalid_ssh_response");
        // Controlled wrapper exit codes are distinct from OpenSSH's transport exit 255.
        if (exitCode is 64 or 65 or 66 or 69) return new("failed", "remote_configuration_invalid");
        if (exitCode == 70) return new(wake ? "unknown" : "failed", "etherwake_failed");
        if (stderr.Contains("Host key verification failed", StringComparison.Ordinal) || stderr.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal) ||
            stderr.Contains("No ", StringComparison.Ordinal) && stderr.Contains("host key is known", StringComparison.Ordinal)) return new("failed", "ssh_host_key_failed");
        if (stderr.Contains("Permission denied", StringComparison.Ordinal) || stderr.Contains("Load key ", StringComparison.Ordinal)) return new("failed", "ssh_auth_failed");
        if (stderr.Contains("Connection refused", StringComparison.Ordinal) || stderr.Contains("No route to host", StringComparison.Ordinal) ||
            stderr.Contains("Network is unreachable", StringComparison.Ordinal) || stderr.Contains("connect to host", StringComparison.Ordinal) && stderr.Contains("timed out", StringComparison.Ordinal))
            return new("failed", "ssh_unreachable");
        // A generic disconnect can occur after the command ran. Never report a definite failure.
        return new(wake ? "unknown" : "failed", "ssh_failed");
    }
    private static bool TunnelPresent(SshSettings ssh)
    {
        try
        {
            var expected = IPAddress.Parse(ssh.BindAddress);
            return NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == ssh.WireGuardInterface &&
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(expected)));
        }
        catch (NetworkInformationException) { return false; }
    }
    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[512];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) return builder.ToString();
            if (builder.Length + count > 4096) throw new InvalidDataException();
            builder.Append(buffer, 0, count);
        }
    }
    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
