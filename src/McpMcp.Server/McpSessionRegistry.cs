using System.Collections.Concurrent;
using McpMcp.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpMcp.Server;

/// <summary>
/// Verzeichnis der aktiven MCP-Sessions (FR-39). Quelle für tools/list_changed-Broadcasts (FR-07)
/// und für die Session-Anzeige im Dashboard (FR-33).
/// </summary>
public sealed class McpSessionRegistry : IActiveSessionSource
{
    private readonly ConcurrentDictionary<McpServer, IdentityId> _sessions = new();

    public int Count => _sessions.Count;

    public int ActiveSessions => _sessions.Count;

    public int ActiveAgents => _sessions.Values.Distinct().Count();

    public void Register(McpServer server, IdentityId identity) => _sessions[server] = identity;

    public void Unregister(McpServer server) => _sessions.TryRemove(server, out _);

    public async Task NotifyToolListChangedAsync(CancellationToken ct)
    {
        foreach (var server in _sessions.Keys)
        {
            try
            {
                await server.SendNotificationAsync(
                    NotificationMethods.ToolListChangedNotification, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Session ist gerade im Abbau — der nächste tools/list-Aufruf holt den Stand ohnehin frisch.
            }
        }
    }
}
