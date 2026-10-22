namespace Fy1Switch.Discovery;

public sealed partial class LanDiscovery
{
    private void Announce(long current, ServiceAnnouncement announcement)
    {
        lock (sync)
        {
            if (current != generation || lifetime is null || lifetime.IsCancellationRequested ||
                !announcement.InstanceName.EndsWith("." + DiscoveryMetadata.ServiceType, StringComparison.OrdinalIgnoreCase)) return;
            if (announcement.Ttl == 0)
            {
                entries.Remove(announcement.InstanceName);
                Publish(current);
                return;
            }
            if ((!entries.ContainsKey(announcement.InstanceName) && entries.Count >= 256) || pending.Count >= 256) return;
            var version = ++revision;
            entries[announcement.InstanceName] = new(version, clock.GetUtcNow().AddSeconds(announcement.Ttl),
                entries.TryGetValue(announcement.InstanceName, out var previous) ? previous.Peers : []);
            var token = lifetime.Token;
            var task = Task.Run(() => ResolveAsync(current, version, announcement.InstanceName, token));
            pending.Add(task);
            _ = task.ContinueWith(completed => { lock (sync) pending.Remove(completed); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task ResolveAsync(long current, long version, string name, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var metadata = await adapter.ResolveAsync(name, interfaceIndex, timeout.Token).ConfigureAwait(false);
            lock (sync)
            {
                if (current != generation || token.IsCancellationRequested || !entries.TryGetValue(name, out var entry) || entry.Revision != version || entry.Expires <= clock.GetUtcNow()) return;
                var peers = metadata is not null && string.Equals(metadata.InstanceName, name, StringComparison.OrdinalIgnoreCase)
                    ? DiscoveryMetadata.Parse(metadata, self, interfaceIndex) : [];
                entries[name] = entry with { Peers = peers };
                Publish(current);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Record(current, Diagnostic("Resolve", error)); }
    }

    private void Expire(long current)
    {
        lock (sync)
        {
            if (current != generation) return;
            foreach (var name in entries.Where(pair => pair.Value.Expires <= clock.GetUtcNow()).Select(pair => pair.Key).ToArray()) entries.Remove(name);
            Publish(current);
        }
    }

    private void Publish(long current)
    {
        var next = entries.Values.OrderBy(entry => entry.Revision).SelectMany(entry => entry.Peers)
            .GroupBy(peer => (peer.SessionId, peer.Address, peer.InterfaceIndex)).Select(group => group.Last())
            .OrderBy(peer => peer.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(peer => peer.SessionId, StringComparer.Ordinal).ThenBy(peer => peer.Address.ToString(), StringComparer.Ordinal).ToArray();
        if (candidates.SequenceEqual(next)) return;
        candidates = Array.AsReadOnly(next);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (sync) { if (current != generation) return; }
            foreach (var handler in CandidatesChanged?.GetInvocationList() ?? [])
                try { ((EventHandler)handler)(this, EventArgs.Empty); }
                catch (Exception error) { Record(current, Diagnostic("CandidatesChanged", error)); }
        });
    }
}
