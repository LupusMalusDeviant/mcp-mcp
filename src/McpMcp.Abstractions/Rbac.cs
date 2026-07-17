namespace McpMcp.Abstractions;

public readonly record struct RoleId(Guid Value)
{
    public static RoleId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct ProfileId(Guid Value)
{
    public static ProfileId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public enum IdentityKind
{
    /// <summary>Maschineller Aufrufer mit API-Key (FR-27).</summary>
    Agent = 0,

    /// <summary>Menschlicher UI-Nutzer (FR-30).</summary>
    User = 1,
}

/// <summary>
/// Ein Allow-Grant (ADR-0006: keine Deny-Grants, Default-Deny macht sie unnötig).
/// Abdeckung: Server==null &amp;&amp; Tool==null → global; Tool!=null → genau dieses Tool
/// (NamespacedToolName trägt den Server-Slug); Server!=null &amp;&amp; Tool==null → ganzer Server.
/// </summary>
public sealed record Grant(PermissionScope Scope, IReadOnlyList<ToolAction> Actions);

/// <summary>Rate-Limit als Rollen-Attribut (FR-31). Wirksam ist das Maximum über alle Rollen einer Identität.</summary>
public sealed record RateLimit(int CallsPerMinute);

public sealed record Role(RoleId Id, string Name, IReadOnlyList<Grant> Grants, RateLimit? RateLimit = null);

/// <summary>Token-Profil einer Identität (ADR-0003): Pinned-Tools voll sichtbar, Rest optional lazy über Meta-Tools.</summary>
public sealed record ToolProfile(
    ProfileId Id,
    string Name,
    IReadOnlyList<NamespacedToolName> PinnedTools,
    bool LazyToolsEnabled);

public sealed record Identity(
    IdentityId Id,
    string Name,
    IdentityKind Kind,
    IReadOnlyList<RoleId> Roles,
    ProfileId? Profile = null);

/// <summary>
/// Lese-Port auf Identitäten/Rollen/Profile. WP2 liefert die In-Memory-Implementierung,
/// WP3 die persistente (ADR-0007). <see cref="Version"/> steigt bei jeder Mutation —
/// Konsumenten (AuthZ-Snapshot-Cache, Katalog) invalidieren darüber.
/// </summary>
public interface IRbacDirectory
{
    long Version { get; }

    event EventHandler? Changed;

    Identity? GetIdentity(IdentityId id);

    Role? GetRole(RoleId id);

    ToolProfile? GetProfile(ProfileId id);
}

/// <summary>Rate-Limit-Prüfung vor dem Invoker (FR-31). False = ablehnen (wird als Denied auditiert).</summary>
public interface IRateLimiter
{
    bool TryAcquire(IdentityId identity);
}
