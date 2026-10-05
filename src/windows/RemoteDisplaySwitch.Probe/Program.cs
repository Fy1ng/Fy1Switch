using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ManagedCommon;
using PowerDisplay.Common;
using PowerDisplay.Common.Drivers;
using PowerDisplay.Common.Drivers.DDC;
using PowerDisplay.Common.Models;
using PowerDisplay.Common.Services;
using PowerDisplay.Models;
using RemoteDisplaySwitch.PowerDisplay;

namespace RemoteDisplaySwitch.Probe;

internal static class Program
{
    private const string UpstreamCommit = "1400fd8e999f381329e16e9df4084f7dc588c8a7";

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 0 || args[0] is "--help" or "help")
        {
            Console.WriteLine("RemoteDisplaySwitch P0: inventory | topology | discover | read <target-key> | on <target-key> --allow-power-write | cycle/restart-cycle <target-key> --allow-power-write [--off-seconds 1..300]");
            Console.WriteLine("cycle defaults to five seconds of DPM (0x04), then attempts ON (0x01). restart-cycle terminates its own writer after OFF and uses a fresh process for ON. Physical identity and visible state must be checked by a person. Recovery is not guaranteed after driver or supervisor failure.");
            Console.WriteLine("--gdi-order-experiment is an explicit P0-only comparison for the G4Q plus MTT VDD clone setup. It does not establish physical identity.");
            return 0;
        }

        if (!ProbeOptions.TryParse(args, out var options))
        {
            Console.Error.WriteLine("Invalid command or arguments. Power writes require an exact target key and --allow-power-write. Use --help.");
            return 2;
        }

        Console.CancelKeyPress += (_, cancel) =>
        {
            cancel.Cancel = true;
            Console.Error.WriteLine("Waiting for the bounded probe and any ON restoration attempt; cancellation does not undo a hardware command.");
        };

        try
        {
            return options!.Worker ? await RunWorkerAsync(options) : await SuperviseAsync(args);
        }
        catch (Exception exception)
        {
            Emit(new { kind = "failure", error = exception.GetType().Name, hardwareOutcome = "unknown" });
            return 10;
        }
    }

    private static ProcessStartInfo CreateWorkerStartInfo(string[] args)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var start = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add("--worker");
        return start;
    }

    private static async Task<int> SuperviseAsync(string[] args)
    {
        PathConstants.EnsurePowerDisplayFolderExists();
        using var supervisorLease = TryAcquireInstance("probe.supervisor.lock");
        if (supervisorLease is null)
        {
            Emit(new { kind = "failure", error = "probe_supervisor_already_running" });
            return 4;
        }

        ProbeOptions.TryParse(args, out var options);
        if (options!.Command == "restart-cycle")
        {
            return await RunRestartCycleAsync(options);
        }

        var timeout = TimeSpan.FromSeconds(90 + (options.Command == "cycle" ? options.OffSeconds : 0));
        var result = await RunWorkerProcessAsync(args, timeout);
        return result.ExitCode;
    }

    private static async Task<ProbeProcessResult> RunWorkerProcessAsync(string[] args, TimeSpan timeout, bool stopAfterOff = false)
    {
        using var child = Process.Start(CreateWorkerStartInfo(args)) ?? throw new InvalidOperationException("Unable to start probe worker.");
        Emit(new { kind = "worker_started", pid = child.Id, stopAfterOff });
        var offAcknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveOutput(string line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var entry = document.RootElement;
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "power_write" &&
                    entry.TryGetProperty("requestedValue", out var requested) && requested.ValueKind == JsonValueKind.Number && requested.TryGetInt32(out var value) && value == 4 &&
                    entry.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.True)
                {
                    offAcknowledged.TrySetResult();
                }
            }
            catch (JsonException)
            {
            }
        }

        var standardOutput = CopyLinesAsync(child.StandardOutput, Console.Out, ObserveOutput);
        var standardError = CopyLinesAsync(child.StandardError, Console.Error);
        var exited = child.WaitForExitAsync();
        var terminated = false;
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            if (stopAfterOff)
            {
                await Task.WhenAny(offAcknowledged.Task, exited).WaitAsync(deadline.Token);
                if (offAcknowledged.Task.IsCompletedSuccessfully && !child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    terminated = true;
                    Emit(new { kind = "writer_terminated_after_off", pid = child.Id });
                }
            }

            await exited.WaitAsync(deadline.Token);
            await Task.WhenAll(standardOutput, standardError);
            return new(terminated ? 0 : child.ExitCode, offAcknowledged.Task.IsCompletedSuccessfully, terminated);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }

            await child.WaitForExitAsync();
            await Task.WhenAll(standardOutput, standardError);
            Emit(new { kind = "failure", error = "worker_timeout", hardwareOutcome = "unknown", retryAutomatically = false });
            return new(3, offAcknowledged.Task.IsCompletedSuccessfully, false);
        }
    }

    private static async Task<int> RunRestartCycleAsync(ProbeOptions options)
    {
        string[] Arguments(string command, int? seconds = null)
        {
            var result = new List<string> { command, options.TargetKey!, "--allow-power-write" };
            if (options.GdiOrderExperiment)
            {
                result.Add("--gdi-order-experiment");
            }

            if (seconds is not null)
            {
                result.AddRange(["--off-seconds", seconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            }

            return result.ToArray();
        }

        Emit(new { kind = "restart_cycle_start", offSeconds = options.OffSeconds, originalHandlesWillBeReleased = true });
        var result = await RestartCycleRunner.RunAsync(
            () => RunWorkerProcessAsync(Arguments("cycle", 300), TimeSpan.FromSeconds(90), stopAfterOff: true),
            duration => Task.Delay(duration),
            async () =>
            {
                Emit(new { kind = "fresh_process_restoration_start" });
                return (await RunWorkerProcessAsync(Arguments("on"), TimeSpan.FromSeconds(90))).ExitCode;
            },
            TimeSpan.FromSeconds(options.OffSeconds));
        Emit(new { kind = "restart_cycle_summary", result.Succeeded, result.Writer, result.RestoreExitCode, physicalPowerValidated = false });
        return result.Succeeded ? 0 : 9;
    }

    private static async Task CopyLinesAsync(StreamReader reader, TextWriter writer, Action<string>? observe = null)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            observe?.Invoke(line);
            try
            {
                await writer.WriteLineAsync(line);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task<int> RunWorkerAsync(ProbeOptions options)
    {
        if (options.Command == "restart-cycle")
        {
            Emit(new { kind = "failure", error = "restart_cycle_requires_supervisor" });
            return 2;
        }

        PathConstants.EnsurePowerDisplayFolderExists();
        using var instance = TryAcquireInstance();
        if (instance is null)
        {
            Emit(new { kind = "failure", error = "probe_already_running" });
            return 4;
        }

        try
        {
            PathConstants.EnsurePowerDisplayFolderExists();
            Logger.Sink = Console.Error.WriteLine;
            Emit(new { kind = "baseline", upstreamCommit = UpstreamCommit, session = Process.GetCurrentProcess().SessionId, readOnly = !options.WritesPower });
            if (options.Command == "topology")
            {
                Emit(new { kind = "topology_diagnostics", report = TopologyDiagnostics.Capture() });
                return 0;
            }

            var inventory = DisplayConfigInventory.GetAllMonitorDisplayInfo();
            foreach (var display in inventory.Values)
            {
                Emit(new
                {
                    kind = "inventory",
                    targetKey = Key(MonitorIdentity.FromDevicePath(display.DevicePath)),
                    name = display.FriendlyName,
                    edidModel = MonitorIdentity.EdidIdFromMonitorId(display.DevicePath),
                    number = display.MonitorNumber,
                    source = display.GdiDeviceName,
                    sharedSourceTargets = inventory.Values.Count(other => other.GdiDeviceName.Equals(display.GdiDeviceName, StringComparison.OrdinalIgnoreCase)),
                });
            }

            if (options.Command == "inventory")
            {
                return inventory.Count > 0 ? 0 : 5;
            }

            if (IsOfficialControllerRunning())
            {
                Emit(new { kind = "failure", error = "official_powerdisplay_running", action = "Pause or exit Power Display before hardware discovery. This tool never stops it." });
                return 6;
            }

            if (File.Exists(PathConstants.DiscoveryLockPath))
            {
                Emit(new { kind = "failure", error = "previous_discovery_interrupted", action = "Review the discovery lock before another hardware attempt. No automatic retry." });
                return 7;
            }

            if (BuiltInMonitorBlacklist.Entries.Count == 0)
            {
                Emit(new { kind = "failure", error = "blacklist_resource_unavailable" });
                return 7;
            }

            using var manager = options.GdiOrderExperiment ? null : new MonitorManager(new MemoryVcpStore());
            using var experimentalController = options.GdiOrderExperiment ? new DdcCiController(new MemoryVcpStore()) : null;
            TopologyDiagnosticReport? experimentalTopology = null;
            IReadOnlyList<global::PowerDisplay.Common.Models.Monitor> monitors;
            if (experimentalController is not null)
            {
                experimentalTopology = TopologyDiagnostics.Capture();
                var orderedTargets = GdiOrderingExperiment.Reorder(inventory.Values.ToArray(), experimentalTopology);
                Emit(new { kind = "ordering_experiment", order = orderedTargets.Select(target => new { name = target.FriendlyName, targetKey = Key(MonitorIdentity.FromDevicePath(target.DevicePath)) }), identityVerified = false });
                monitors = (await experimentalController.DiscoverMonitorsAsync(orderedTargets)).ToArray();
            }
            else
            {
                monitors = await manager!.DiscoverMonitorsAsync();
            }
            foreach (var monitor in monitors)
            {
                Emit(new
                {
                    kind = "discovered",
                    targetKey = Key(monitor.Id),
                    name = monitor.Name,
                    capabilityModel = monitor.VcpCapabilitiesInfo?.Model,
                    method = monitor.CommunicationMethod,
                    supportsPower = monitor.SupportsPowerState,
                    supportedPowerValues = monitor.SupportedPowerStates,
                    powerReadDuringDiscovery = monitor.ReadValues.HasFlag(MonitorReadFlags.PowerState),
                    observedPowerValue = monitor.ReadValues.HasFlag(MonitorReadFlags.PowerState) ? (int?)monitor.CurrentPowerState : null,
                    identityVerified = false,
                });
            }

            if (options.TargetKey is not null)
            {
                var candidates = monitors.Where(monitor => Key(monitor.Id).Equals(options.TargetKey, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length != 1 || candidates[0].CommunicationMethod != "DDC/CI")
                {
                    Emit(new { kind = "failure", error = "target_not_unique_or_not_ddc" });
                    return 8;
                }

                using var reader = new DdcCiController(new MemoryVcpStore());
                var value = await reader.ReadPowerObservationAsync(candidates[0]);
                Emit(new { kind = "power_read", targetKey = options.TargetKey, observation = value, source = value.Succeeded ? "hardware_read" : "unavailable" });
                if (options.Command == "read")
                {
                    return value.Succeeded ? 0 : 9;
                }

                if (options.GdiOrderExperiment && monitors.Count != 1)
                {
                    Emit(new { kind = "failure", error = "experiment_requires_one_discovered_ddc_candidate" });
                    return 12;
                }

                Task<MonitorOperationResult> WritePowerAsync(int requestedValue) => experimentalController is not null
                    ? experimentalController.SetPowerStateAsync(candidates[0], requestedValue)
                    : manager!.SetPowerStateAsync(candidates[0].Id, requestedValue);
                return await RunPowerTestAsync(options, WritePowerAsync, reader, candidates[0], value, inventory, experimentalTopology);
            }

            Emit(new { kind = "summary", inventoryCount = inventory.Count, discoveredCount = monitors.Count, physicalPowerValidated = false });
            return monitors.Count > 0 ? 0 : 5;
        }
        catch (Exception exception)
        {
            Emit(new { kind = "failure", error = exception.GetType().Name, hardwareOutcome = "unknown" });
            return 10;
        }
    }

    private static FileStream? TryAcquireInstance(string fileName = "probe.instance.lock")
    {
        try
        {
            return new FileStream(Path.Combine(PathConstants.PowerDisplayFolderPath, fileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<int> RunPowerTestAsync(
        ProbeOptions options,
        Func<int, Task<MonitorOperationResult>> writePowerAsync,
        DdcCiController reader,
        global::PowerDisplay.Common.Models.Monitor monitor,
        PowerReadObservation before,
        Dictionary<string, MonitorDisplayInfo> inventory,
        TopologyDiagnosticReport? experimentalTopology)
    {
        if (!options.GdiOrderExperiment && inventory.Values.GroupBy(display => display.GdiDeviceName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            Emit(new { kind = "failure", error = "clone_binding_unverified", action = "Default ordinal binding is not safe for power writes in a shared-source topology." });
            return 12;
        }

        var supported = monitor.SupportedPowerStates;
        if (supported is null || !supported.Contains(1) || (options.Command == "cycle" && !supported.Contains(4)))
        {
            Emit(new { kind = "failure", error = "required_power_values_not_advertised" });
            return 11;
        }

        if (options.Command == "cycle" && (!before.Succeeded || before.Value != 1))
        {
            Emit(new { kind = "failure", error = "cycle_requires_fresh_on_observation", action = "Do not infer that a failed read means OFF." });
            return 11;
        }

        var current = DisplayConfigInventory.GetAllMonitorDisplayInfo();
        if (experimentalTopology is not null && JsonSerializer.Serialize(experimentalTopology) != JsonSerializer.Serialize(TopologyDiagnostics.Capture()))
        {
            Emit(new { kind = "failure", error = "experimental_topology_changed" });
            return 12;
        }

        if (!InventorySignature(inventory).Equals(InventorySignature(current), StringComparison.Ordinal) || IsOfficialControllerRunning())
        {
            Emit(new { kind = "failure", error = "topology_changed_or_other_controller_started" });
            return 11;
        }

        Emit(new { kind = "power_test_start", targetKey = options.TargetKey, name = monitor.Name, identityVerified = false, physicalConfirmationRequired = true });
        MonitorOperationResult? offResult = null;
        MonitorOperationResult onResult;
        if (options.Command == "cycle")
        {
            var cycle = await PowerCycleRunner.RunAsync(
                async requestedValue =>
                {
                    if (requestedValue == 1)
                    {
                        Emit(new { kind = "restoration_attempt", requestedValue });
                    }

                    var result = await writePowerAsync(requestedValue);
                    Emit(new { kind = "power_write", requestedValue, accepted = result.IsSuccess, errorCode = result.ErrorCode, physicallyVerified = false });
                    return result;
                },
                duration => Task.Delay(duration),
                TimeSpan.FromSeconds(options.OffSeconds));
            offResult = cycle.Off;
            onResult = cycle.On;
        }
        else
        {
            onResult = await writePowerAsync(1);
            Emit(new { kind = "power_write", requestedValue = 1, accepted = onResult.IsSuccess, errorCode = onResult.ErrorCode, physicallyVerified = false });
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var after = await reader.ReadPowerObservationAsync(monitor);
        Emit(new { kind = "power_read", targetKey = options.TargetKey, observation = after, source = after.Succeeded ? "hardware_read" : "unavailable" });
        var onReadBack = after.Succeeded && after.Value == 1;
        Emit(new { kind = "power_test_summary", onReadBack, physicalPowerValidated = false, identityVerified = false, needsHumanObservation = true });
        return onResult.IsSuccess && onReadBack && (offResult is null || offResult.Value.IsSuccess) ? 0 : 9;
    }

    private static string InventorySignature(Dictionary<string, MonitorDisplayInfo> inventory) =>
        string.Join("|", inventory.Values.Select(display => $"{display.DevicePath}:{display.GdiDeviceName}:{display.MonitorNumber}").Order(StringComparer.OrdinalIgnoreCase));

    private static bool IsOfficialControllerRunning()
    {
        var processes = Process.GetProcessesByName("PowerToys.PowerDisplay").Concat(Process.GetProcessesByName("RemoteDisplaySwitch.Worker")).ToArray();
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static string Key(string identifier) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identifier.ToUpperInvariant()))).ToLowerInvariant();

    private static void Emit<T>(T value)
    {
        try
        {
            Console.WriteLine(JsonSerializer.Serialize(value));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }
}
