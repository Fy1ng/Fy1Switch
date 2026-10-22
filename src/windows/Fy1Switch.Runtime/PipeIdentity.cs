using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Fy1Switch.Runtime;

public static class PipeIdentity
{
    public static string DefaultHostPipe
    {
        get
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new ControlException("user_identity_unavailable");
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..16];
            return $"Fy1Switch.Host.{Process.GetCurrentProcess().SessionId}.{key}";
        }
    }
    public static void RequireClientProcess(NamedPipeServerStream pipe, int expectedPid)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != expectedPid) throw new ControlException("worker_identity_mismatch");
    }
    public static void RequireServerPath(NamedPipeClientStream pipe, string expectedPath)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) throw new ControlException("host_identity_unavailable");
        using var process = Process.GetProcessById((int)pid);
        var actual = process.MainModule?.FileName;
        if (actual is null || !Path.GetFullPath(actual).Equals(Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase)) throw new ControlException("host_identity_mismatch");
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
