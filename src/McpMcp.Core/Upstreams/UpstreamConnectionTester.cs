using McpMcp.Abstractions;

namespace McpMcp.Core.Upstreams;

/// <summary>Transienter Verbindungstest (FR-34): connect → discover → dispose, mit kurzem Timeout, ohne Registrierung.</summary>
public sealed class UpstreamConnectionTester : IUpstreamConnectionTester
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private readonly Dictionary<UpstreamTransportKind, IUpstreamConnector> _connectors;

    public UpstreamConnectionTester(IEnumerable<IUpstreamConnector> connectors)
    {
        ArgumentNullException.ThrowIfNull(connectors);
        _connectors = connectors.ToDictionary(c => c.Kind);
    }

    public async Task<UpstreamTestResult> TestAsync(UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        try
        {
            UpstreamConfigValidator.Validate(config);
        }
        catch (ArgumentException ex)
        {
            return new UpstreamTestResult(false, 0, ex.Message);
        }

        if (!_connectors.TryGetValue(config.Kind, out var connector))
        {
            return new UpstreamTestResult(false, 0, $"Kein Connector für Transport {config.Kind}.");
        }

        using var timeoutCts = new CancellationTokenSource(TestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await using var connection = await connector.ConnectAsync(ServerId.New(), config, linked.Token).ConfigureAwait(false);
            var inventory = await connection.DiscoverAsync(linked.Token).ConfigureAwait(false);
            return new UpstreamTestResult(true, inventory.Tools.Count, null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return new UpstreamTestResult(false, 0, $"Zeitüberschreitung nach {TestTimeout.TotalSeconds:0} s.");
        }
        catch (Exception ex)
        {
            return new UpstreamTestResult(false, 0, ex.Message);
        }
    }
}
