using System.Text.Json;

namespace McpMcp.Abstractions;

public enum AuditEventKind
{
    ToolCall = 0,
    ServerLifecycle = 1,
    ConfigChanged = 2,
    RbacChanged = 3,
    Authentication = 4,
    AssetChanged = 5,
}

/// <summary>
/// Ein Audit-Ereignis (FR-21/22). <see cref="RedactedArguments"/> ist IMMER bereits durch
/// <see cref="IRedactionService"/> gelaufen — ungefilterte Argumente dürfen diesen Typ nie erreichen (DON'T Nr. 2).
/// </summary>
public sealed record AuditEvent(
    DateTimeOffset Timestamp,
    IdentityId? Caller,
    CallOrigin Origin,
    AuditEventKind Kind,
    ServerId? Server,
    string? Tool,
    InvocationStatus? Status,
    JsonElement? RedactedArguments,
    long? RequestBytes,
    long? ResponseBytes,
    TimeSpan? Duration);

/// <summary>Filter für die Log-Ansicht/den Export (FR-23).</summary>
public sealed record AuditFilter(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    IdentityId? Caller = null,
    ServerId? Server = null,
    string? Tool = null,
    InvocationStatus? Status = null,
    AuditEventKind? Kind = null,
    int Page = 1,
    int PageSize = 100);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, long TotalCount, int Page, int PageSize);

/// <summary>
/// Hot-Path-Seite des Audits: <see cref="Record"/> ist synchron, nicht-blockierend (Channel-Enqueue)
/// und darf einen Tool-Call NIE verzögern oder scheitern lassen (ADR-0007, DO Nr. 5 / DON'T Nr. 3).
/// </summary>
public interface IAuditSink
{
    void Record(AuditEvent evt);
}

/// <summary>Abfrage-Seite des Audits für UI und Export (FR-23).</summary>
public interface IAuditQuery
{
    Task<PagedResult<AuditEvent>> QueryAsync(AuditFilter filter, CancellationToken ct);
}

/// <summary>Maskiert Secret-Felder in Tool-Argumenten vor der Persistierung (FR-24). Mutiert das Original nie.</summary>
public interface IRedactionService
{
    JsonElement RedactArguments(NamespacedToolName tool, JsonElement args);
}
