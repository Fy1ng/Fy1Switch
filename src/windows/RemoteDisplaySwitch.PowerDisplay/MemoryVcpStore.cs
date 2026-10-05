using System;
using System.Collections.Generic;
using System.Linq;
using PowerDisplay.Common.Interfaces;
using PowerDisplay.Common.Models;

namespace RemoteDisplaySwitch.PowerDisplay;

public sealed class MemoryVcpStore : IKnownGoodVcpStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<byte, KnownGoodVcpFeature>> _features = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<byte, KnownGoodVcpFeature> GetKnownGoodFeatures(string monitorId)
    {
        lock (_gate)
        {
            return _features.TryGetValue(monitorId, out var values)
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone())
                : new Dictionary<byte, KnownGoodVcpFeature>();
        }
    }

    public void UpsertKnownGoodFeature(string monitorId, KnownGoodVcpFeature feature)
    {
        lock (_gate)
        {
            if (!_features.TryGetValue(monitorId, out var values))
            {
                values = new Dictionary<byte, KnownGoodVcpFeature>();
                _features.Add(monitorId, values);
            }

            values[feature.Code] = feature.Clone();
        }
    }
}
