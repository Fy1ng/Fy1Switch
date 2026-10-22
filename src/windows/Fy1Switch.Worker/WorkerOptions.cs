using System.Globalization;

namespace Fy1Switch.Worker;

internal sealed record WorkerOptions(string PipeName, int ParentPid, string DataDirectory, bool Simulate)
{
    public static bool TryParse(string[] args, out WorkerOptions? options)
    {
        options = null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var simulate = false;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument == "--simulate" && !simulate)
            {
                simulate = true;
                continue;
            }

            if (argument is not ("--pipe" or "--parent-pid" or "--data-dir") ||
                ++index >= args.Length || !values.TryAdd(argument, args[index]))
            {
                return false;
            }
        }

        if (!values.TryGetValue("--pipe", out var pipe) || pipe.Length is < 1 or > 128 ||
            pipe.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')) ||
            !values.TryGetValue("--parent-pid", out var parent) ||
            !int.TryParse(parent, NumberStyles.None, CultureInfo.InvariantCulture, out var parentPid) || parentPid <= 0 ||
            !values.TryGetValue("--data-dir", out var directory) || string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var officialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "PowerToys");
            if (fullDirectory.Equals(officialDirectory, StringComparison.OrdinalIgnoreCase) ||
                fullDirectory.StartsWith(officialDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            options = new(pipe, parentPid, fullDirectory, simulate);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }
}
