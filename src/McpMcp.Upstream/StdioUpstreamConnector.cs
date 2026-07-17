using McpMcp.Abstractions;
using ModelContextProtocol.Client;

namespace McpMcp.Upstream;

/// <summary>Startet lokale MCP-Server als Kindprozesse via stdio (ADR-0005).</summary>
public sealed class StdioUpstreamConnector : IUpstreamConnector
{
    public UpstreamTransportKind Kind => UpstreamTransportKind.Stdio;

    public async Task<IUpstreamConnection> ConnectAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = config.Stdio
            ?? throw new ArgumentException($"Config '{config.Slug}' hat keine Stdio-Optionen.", nameof(config));

        ProcessHygiene.EnsureInitialized();

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = config.Slug,
            Command = options.Command,
            Arguments = [.. options.Arguments],
            WorkingDirectory = options.WorkingDirectory,
            EnvironmentVariables = options.EnvironmentVariables?.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
        });

        var client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
        return new SdkUpstreamConnection(id, client);
    }
}
