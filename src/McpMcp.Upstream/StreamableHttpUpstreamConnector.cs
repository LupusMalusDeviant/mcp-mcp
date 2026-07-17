using McpMcp.Abstractions;
using ModelContextProtocol.Client;

namespace McpMcp.Upstream;

/// <summary>Verbindet Remote-MCP-Server über Streamable HTTP (FR-02).</summary>
public sealed class StreamableHttpUpstreamConnector : IUpstreamConnector
{
    public UpstreamTransportKind Kind => UpstreamTransportKind.StreamableHttp;

    public async Task<IUpstreamConnection> ConnectAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = config.Http
            ?? throw new ArgumentException($"Config '{config.Slug}' hat keine Http-Optionen.", nameof(config));

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = config.Slug,
            Endpoint = options.Endpoint,
            AdditionalHeaders = options.Headers?.ToDictionary(kv => kv.Key, kv => kv.Value),
        });

        var client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
        return new SdkUpstreamConnection(id, client);
    }
}
