using System.Collections.Concurrent;
using McpMcp.Abstractions;

namespace McpMcp.Core.Rbac;

/// <summary>
/// Einzige Autorisierungsquelle (ADR-0006): Default-Deny, Sichtbarkeit folgt Berechtigung (FR-29).
/// Pro Identität wird ein Berechtigungs-Snapshot kompiliert und gecacht; der Cache invalidiert
/// über <see cref="IRbacDirectory.Version"/>. Evaluate/FilterVisible sind danach reine O(1)-Lookups.
/// </summary>
public sealed class AuthorizationService : IAuthorizationService
{
    private readonly IRbacDirectory _directory;
    private readonly ConcurrentDictionary<IdentityId, CachedSnapshot> _cache = new();

    public AuthorizationService(IRbacDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
    }

    public AuthorizationDecision Evaluate(IdentityId identity, PermissionScope scope, ToolAction action)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var snapshot = GetSnapshot(identity);
        if (snapshot is null)
        {
            return AuthorizationDecision.Deny($"Identität {identity} ist nicht registriert.");
        }

        return snapshot.Covers(scope, action)
            ? AuthorizationDecision.Allow
            : AuthorizationDecision.Deny(
                $"Kein Grant für {action} auf {(scope.Tool?.ToString() ?? scope.Server?.ToString() ?? "global")} (Default-Deny).");
    }

    public IReadOnlyList<CatalogEntry> FilterVisible(IdentityId identity, IReadOnlyList<CatalogEntry> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var snapshot = GetSnapshot(identity);
        if (snapshot is null)
        {
            return [];
        }

        var visible = new List<CatalogEntry>(catalog.Count);
        foreach (var entry in catalog)
        {
            var action = entry.Kind switch
            {
                CatalogEntryKind.Resource => ToolAction.ReadResource,
                CatalogEntryKind.Prompt => ToolAction.UsePrompt,
                _ => ToolAction.UseTool,
            };
            if (snapshot.Covers(new PermissionScope(entry.Server, entry.Name), action))
            {
                visible.Add(entry);
            }
        }

        return visible;
    }

    private CompiledPermissions? GetSnapshot(IdentityId identity)
    {
        var version = _directory.Version;
        if (_cache.TryGetValue(identity, out var cached) && cached.Version == version)
        {
            return cached.Permissions;
        }

        var compiled = Compile(identity);
        if (compiled is null)
        {
            _cache.TryRemove(identity, out _);
            return null;
        }

        _cache[identity] = new CachedSnapshot(version, compiled);
        return compiled;
    }

    private CompiledPermissions? Compile(IdentityId identityId)
    {
        var identity = _directory.GetIdentity(identityId);
        if (identity is null)
        {
            return null;
        }

        var global = new HashSet<ToolAction>();
        var perServer = new Dictionary<ServerId, HashSet<ToolAction>>();
        var perTool = new Dictionary<NamespacedToolName, HashSet<ToolAction>>();

        foreach (var roleId in identity.Roles)
        {
            var role = _directory.GetRole(roleId);
            if (role is null)
            {
                continue; // verwaiste Rollen-Referenz gewährt nichts (Default-Deny)
            }

            foreach (var grant in role.Grants)
            {
                var target = grant.Scope switch
                {
                    { Tool: { } tool } => perTool.TryGetValue(tool, out var set)
                        ? set
                        : perTool[tool] = [],
                    { Server: { } server } => perServer.TryGetValue(server, out var set)
                        ? set
                        : perServer[server] = [],
                    _ => global,
                };
                foreach (var action in grant.Actions)
                {
                    target.Add(action);
                }
            }
        }

        return new CompiledPermissions(global, perServer, perTool);
    }

    private sealed record CachedSnapshot(long Version, CompiledPermissions Permissions);

    private sealed class CompiledPermissions
    {
        private readonly HashSet<ToolAction> _global;
        private readonly Dictionary<ServerId, HashSet<ToolAction>> _perServer;
        private readonly Dictionary<NamespacedToolName, HashSet<ToolAction>> _perTool;

        public CompiledPermissions(
            HashSet<ToolAction> global,
            Dictionary<ServerId, HashSet<ToolAction>> perServer,
            Dictionary<NamespacedToolName, HashSet<ToolAction>> perTool)
        {
            _global = global;
            _perServer = perServer;
            _perTool = perTool;
        }

        public bool Covers(PermissionScope scope, ToolAction action)
        {
            if (_global.Contains(action))
            {
                return true;
            }

            if (scope.Server is { } server
                && _perServer.TryGetValue(server, out var serverActions)
                && serverActions.Contains(action))
            {
                return true; // Server-Grant vererbt auf alle Tools darunter (FR-28)
            }

            return scope.Tool is { } tool
                && _perTool.TryGetValue(tool, out var toolActions)
                && toolActions.Contains(action);
        }
    }
}
