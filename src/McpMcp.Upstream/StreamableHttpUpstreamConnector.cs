using McpMcp.Abstractions;
using ModelContextProtocol.Client;

namespace McpMcp.Upstream;

/// <summary>Verbindet Remote-MCP-Server über Streamable HTTP (FR-02); markiert ausgehende Calls für die Loop-Erkennung (FR-05).</summary>
public sealed class StreamableHttpUpstreamConnector : IUpstreamConnector
{
    private readonly GatewayIdentity? _gatewayIdentity;

    public StreamableHttpUpstreamConnector(GatewayIdentity? gatewayIdentity = null)
    {
        _gatewayIdentity = gatewayIdentity;
    }

    public UpstreamTransportKind Kind => UpstreamTransportKind.StreamableHttp;

    public async Task<IUpstreamConnection> ConnectAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = config.Http
            ?? throw new ArgumentException($"Config '{config.Slug}' hat keine Http-Optionen.", nameof(config));

        var headers = options.Headers?.ToDictionary(kv => kv.Key, kv => kv.Value)
            ?? new Dictionary<string, string>();
        if (_gatewayIdentity is not null)
        {
            headers[GatewayIdentity.InstanceHeader] = _gatewayIdentity.InstanceId;
        }

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = config.Slug,
            Endpoint = options.Endpoint,
            AdditionalHeaders = headers,
            // FR-02: explizit gesetzt statt auf den SDK-Default zu vertrauen — AutoDetect probiert
            // Streamable HTTP und fällt auf HTTP+SSE zurück. Ein SDK-Upgrade darf das nicht
            // stillschweigend ändern; der Default ist zusätzlich per Test festgenagelt.
            TransportMode = options.AllowLegacySse
                ? HttpTransportMode.AutoDetect
                : HttpTransportMode.StreamableHttp,
        });

        var client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
        return new SdkUpstreamConnection(id, client);
    }
}
