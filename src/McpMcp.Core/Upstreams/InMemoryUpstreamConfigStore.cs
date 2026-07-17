using System.Collections.Concurrent;
using McpMcp.Abstractions;

namespace McpMcp.Core.Upstreams;

/// <summary>WP1.3-Stub: versionierte Configs im Speicher. Wird in WP3 durch die EF-Core-Implementierung ersetzt (ADR-0007).</summary>
public sealed class InMemoryUpstreamConfigStore : IUpstreamConfigStore
{
    private readonly ConcurrentDictionary<ServerId, List<UpstreamConfigVersion>> _histories = new();
    private readonly TimeProvider _time;

    public InMemoryUpstreamConfigStore(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    public Task<ConfigVersionId> AppendVersionAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var history = _histories.GetOrAdd(id, static _ => []);
        lock (history)
        {
            var version = new ConfigVersionId(history.Count + 1);
            history.Add(new UpstreamConfigVersion(version, config, _time.GetUtcNow()));
            return Task.FromResult(version);
        }
    }

    public Task<UpstreamServerConfig?> GetVersionAsync(ServerId id, ConfigVersionId version, CancellationToken ct)
    {
        if (_histories.TryGetValue(id, out var history))
        {
            lock (history)
            {
                var match = history.FirstOrDefault(v => v.Version == version);
                return Task.FromResult(match?.Config);
            }
        }

        return Task.FromResult<UpstreamServerConfig?>(null);
    }

    public Task<IReadOnlyList<UpstreamConfigVersion>> GetHistoryAsync(ServerId id, CancellationToken ct)
    {
        if (_histories.TryGetValue(id, out var history))
        {
            lock (history)
            {
                return Task.FromResult<IReadOnlyList<UpstreamConfigVersion>>([.. history]);
            }
        }

        return Task.FromResult<IReadOnlyList<UpstreamConfigVersion>>([]);
    }

    public Task RemoveAsync(ServerId id, CancellationToken ct)
    {
        _histories.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}
