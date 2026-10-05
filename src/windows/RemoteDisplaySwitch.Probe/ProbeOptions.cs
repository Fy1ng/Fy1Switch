namespace RemoteDisplaySwitch.Probe;

public sealed record ProbeOptions(string Command, string? TargetKey, bool Worker, bool AllowPowerWrite, bool GdiOrderExperiment = false)
{
    public bool WritesPower => Command is "on" or "cycle";

    public static bool TryParse(string[] arguments, out ProbeOptions? options)
    {
        options = null;
        if (arguments.Length == 0)
        {
            return false;
        }

        var command = arguments[0];
        if (command is not ("inventory" or "topology" or "discover" or "read" or "on" or "cycle"))
        {
            return false;
        }

        var needsTarget = command is "read" or "on" or "cycle";
        var position = 1;
        string? target = null;
        if (needsTarget)
        {
            if (arguments.Length < 2 || arguments[1].Length != 64 || !arguments[1].All(Uri.IsHexDigit))
            {
                return false;
            }

            target = arguments[position++].ToLowerInvariant();
        }

        var worker = false;
        var allowWrite = false;
        var gdiOrderExperiment = false;
        while (position < arguments.Length)
        {
            switch (arguments[position++])
            {
                case "--worker" when !worker:
                    worker = true;
                    break;
                case "--allow-power-write" when !allowWrite:
                    allowWrite = true;
                    break;
                case "--gdi-order-experiment" when !gdiOrderExperiment:
                    gdiOrderExperiment = true;
                    break;
                default:
                    return false;
            }
        }

        var writesPower = command is "on" or "cycle";
        if (writesPower != allowWrite || (gdiOrderExperiment && command is "inventory" or "topology"))
        {
            return false;
        }

        options = new(command, target, worker, allowWrite, gdiOrderExperiment);
        return true;
    }
}
