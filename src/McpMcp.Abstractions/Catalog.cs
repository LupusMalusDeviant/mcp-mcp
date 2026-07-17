using System.Text.Json;

namespace McpMcp.Abstractions;

public enum CatalogEntryKind
{
    Tool = 0,
    Resource = 1,
    Prompt = 2,

    /// <summary>Eingebautes Gateway-Meta-Tool (search_tools, describe_tool, invoke_tool; FR-12).</summary>
    MetaTool = 3,
}

/// <summary>Ein aggregierter, namespaced Katalog-Eintrag inkl. Token-Schätzung für das Cockpit (FR-15).</summary>
public sealed record CatalogEntry(
    NamespacedToolName Name,
    ServerId Server,
    string Description,
    JsonElement InputSchema,
    CatalogEntryKind Kind,
    int EstimatedSchemaTokens);

/// <summary>Profil-Sicht einer Identität: Pinned-Tools voll sichtbar, Rest optional lazy (ADR-0003).</summary>
public sealed record ProfileView(
    IReadOnlyList<CatalogEntry> PinnedTools,
    bool LazyToolsEnabled,
    int EstimatedContextTokens);

/// <summary>Kompakter Treffer für <c>search_tools</c> — bewusst ohne volles Schema (Token-Sparen).</summary>
public sealed record ToolSearchHit(NamespacedToolName Name, string ShortDescription, double Score);

public enum CatalogChangeKind
{
    ServerAdded = 0,
    ServerRemoved = 1,
    InventoryChanged = 2,
    PermissionsChanged = 3,
}

public sealed class CatalogChangedEventArgs : EventArgs
{
    public required CatalogChangeKind Kind { get; init; }
    public required IReadOnlyList<ServerId> AffectedServers { get; init; }
}

/// <summary>
/// Aggregierter Gesamtkatalog aller Healthy-Upstreams mit Profil-/RBAC-Sichten.
/// <see cref="Changed"/> ist der Auslöser für <c>notifications/tools/list_changed</c> (FR-07).
/// </summary>
public interface IToolCatalog
{
    /// <summary>Unveränderlicher Snapshot des Gesamtkatalogs (immutable-swap, DON'T Nr. 7).</summary>
    IReadOnlyList<CatalogEntry> Snapshot { get; }

    /// <summary>Sicht einer Identität gemäß Profil und RBAC-Filter.</summary>
    ProfileView GetViewFor(IdentityId identity);

    /// <summary>Keyword-Suche über Name + Beschreibung, RBAC-gefiltert (FR-12, FR-29).</summary>
    IReadOnlyList<ToolSearchHit> Search(IdentityId identity, string query, int limit);

    /// <summary>Schneller Eintrag-Lookup fürs Routing (WP4); null wenn unbekannt. Ohne RBAC-Prüfung — die macht der Invoker.</summary>
    CatalogEntry? Find(NamespacedToolName name);

    event EventHandler<CatalogChangedEventArgs>? Changed;
}
