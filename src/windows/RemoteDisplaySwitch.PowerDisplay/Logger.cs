using System;
using System.Text.RegularExpressions;

namespace ManagedCommon;

public static partial class Logger
{
    public static Action<string>? Sink { get; set; }

    public static void LogInfo(string message) => Write("info", message);
    public static void LogWarning(string message) => Write("warning", message);
    public static void LogError(string message) => Write("error", message);
    public static void LogDebug(string message) => Write("debug", message);
    public static void LogTrace(string message) => Write("trace", message);

    private static void Write(string level, string message)
    {
        var sanitized = DevicePath().Replace(message, "<display-id>");
        sanitized = Handle().Replace(sanitized, "handle=<redacted>");
        Sink?.Invoke($"{DateTimeOffset.UtcNow:O} [{level}] {sanitized}");
    }

    [GeneratedRegex(@"\\\\\?\\DISPLAY#[^\s\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex DevicePath();

    [GeneratedRegex(@"handle=0x[0-9a-f]+", RegexOptions.IgnoreCase)]
    private static partial Regex Handle();
}
