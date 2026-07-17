using System.Text.Json;

namespace McpMcp.Abstractions;

public enum UpstreamTransportKind
{
    Stdio = 0,
    StreamableHttp = 1,
    OpenApi = 2,
}

public enum UpstreamState
{
    Starting = 0,
    Healthy = 1,
    Degraded = 2,
    Stopped = 3,
    Failed = 4,
}

public enum OpenApiAuthKind
{
    None = 0,
    ApiKeyHeader = 1,
    Bearer = 2,
    Basic = 3,
}

public sealed record StdioTransportOptions(
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    string? WorkingDirectory = null);

public sealed record HttpTransportOptions(
    Uri Endpoint,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>OpenAPI-Quelle als virtueller Upstream (FR-19). Credentials nie inline — nur als Referenz auf den verschlüsselten Store (NFR-04).</summary>
public sealed record OpenApiTransportOptions(
    Uri SpecLocation,
    Uri? BaseAddress = null,
    OpenApiAuthKind AuthKind = OpenApiAuthKind.None,
    string? CredentialReference = null);

public sealed record RestartPolicy(
    int MaxRetries,
    TimeSpan InitialBackoff,
    double BackoffMultiplier,
    TimeSpan MaxBackoff)
{
    public static RestartPolicy Default { get; } = new(5, TimeSpan.FromSeconds(1), 2.0, TimeSpan.FromMinutes(1));
}

/// <summary>
/// Vollständige Konfiguration eines Upstream-Servers. Genau eines der Transport-Options-Felder
/// muss gesetzt sein und zu <see cref="Kind"/> passen; <see cref="Slug"/> ist die Namespacing-Basis (FR-03).
/// </summary>
public sealed record UpstreamServerConfig(
    string Slug,
    string DisplayName,
    UpstreamTransportKind Kind,
    bool Enabled,
    StdioTransportOptions? Stdio = null,
    HttpTransportOptions? Http = null,
    OpenApiTransportOptions? OpenApi = null,
    RestartPolicy? Restart = null,
    TimeSpan? CallTimeout = null);

public sealed record ToolDescriptor(string Name, string? Description, JsonElement InputSchema);

public sealed record ResourceDescriptor(Uri Uri, string Name, string? Description, string? MimeType);

public sealed record PromptDescriptor(string Name, string? Description);

/// <summary>Discovery-Ergebnis eines Upstreams: Tools, Resources und Prompts (FR-04).</summary>
public sealed record UpstreamInventory(
    IReadOnlyList<ToolDescriptor> Tools,
    IReadOnlyList<ResourceDescriptor> Resources,
    IReadOnlyList<PromptDescriptor> Prompts);

public sealed record UpstreamStatus(
    ServerId Id,
    string Slug,
    UpstreamState State,
    string? LastError,
    int ToolCount,
    DateTimeOffset LastHealthyAt);

/// <summary>Drain-Verhalten beim Entfernen/Stoppen unter Last (WP1.4): Gnadenfrist für In-Flight-Calls, danach Cancel.</summary>
public sealed record DrainPolicy(TimeSpan GracePeriod)
{
    public static DrainPolicy Immediate { get; } = new(TimeSpan.Zero);
    public static DrainPolicy Graceful(TimeSpan gracePeriod) => new(gracePeriod);
}

public sealed class UpstreamNotificationEventArgs : EventArgs
{
    public required ServerId Server { get; init; }
    public required string Method { get; init; }
    public JsonElement? Params { get; init; }
}

public enum UpstreamChangeKind
{
    Added = 0,
    Removed = 1,
    InventoryChanged = 2,
    StateChanged = 3,
}

/// <summary>Signal des Supervisors an den Katalog (WP2) und die UI. Auslöser für tools/list_changed (FR-07).</summary>
public sealed class UpstreamChangedEventArgs : EventArgs
{
    public required ServerId Server { get; init; }
    public required UpstreamChangeKind Kind { get; init; }
    public required UpstreamState State { get; init; }
}

/// <summary>Ein Eintrag der Konfigurations-Historie eines Upstream-Servers (FR-10).</summary>
public sealed record UpstreamConfigVersion(ConfigVersionId Version, UpstreamServerConfig Config, DateTimeOffset SavedAt);

/// <summary>
/// Persistenz-Port für versionierte Upstream-Konfigurationen (FR-10). WP1 liefert einen
/// In-Memory-Stub in Core; die EF-Core-Implementierung kommt mit WP3 (ADR-0007).
/// </summary>
public interface IUpstreamConfigStore
{
    /// <summary>Hängt eine neue Version an (append-only) und liefert deren Versionsnummer.</summary>
    Task<ConfigVersionId> AppendVersionAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct);

    Task<UpstreamServerConfig?> GetVersionAsync(ServerId id, ConfigVersionId version, CancellationToken ct);

    /// <summary>Historie aufsteigend nach Version; leer, wenn der Server unbekannt ist.</summary>
    Task<IReadOnlyList<UpstreamConfigVersion>> GetHistoryAsync(ServerId id, CancellationToken ct);

    /// <summary>Jeweils neueste Version aller bekannten Server — Grundlage für den Startup-Restore (WP4.2).</summary>
    Task<IReadOnlyDictionary<ServerId, UpstreamConfigVersion>> GetAllLatestAsync(CancellationToken ct);

    /// <summary>Entfernt die komplette Historie eines Servers (bei endgültigem Remove).</summary>
    Task RemoveAsync(ServerId id, CancellationToken ct);
}

/// <summary>Fabrik pro Transporttyp. Implementierungen: Stdio, StreamableHttp, OpenApi (ADR-0005/0008).</summary>
public interface IUpstreamConnector
{
    UpstreamTransportKind Kind { get; }

    /// <summary>Baut eine Verbindung auf. <paramref name="id"/> wird vom Supervisor vergeben und identifiziert die Verbindung in Events.</summary>
    Task<IUpstreamConnection> ConnectAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct);
}

/// <summary>
/// Eine aktive Verbindung zu einem Upstream. Kapselt das MCP-SDK vollständig —
/// oberhalb dieses Interfaces existieren keine SDK-Typen (DON'T Nr. 1).
/// </summary>
public interface IUpstreamConnection : IAsyncDisposable
{
    ServerId Id { get; }

    Task<UpstreamInventory> DiscoverAsync(CancellationToken ct);

    Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct);

    /// <summary>Liest eine Resource des Upstreams (FR-04); Ergebnis ist das serialisierte ReadResourceResult.</summary>
    Task<JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct);

    /// <summary>Holt einen Prompt des Upstreams (FR-04); Ergebnis ist das serialisierte GetPromptResult.</summary>
    Task<JsonElement> GetPromptAsync(string promptName, JsonElement? args, CancellationToken ct);

    /// <summary>Health-Probe (MCP ping). Wirft bei totem Upstream.</summary>
    Task PingAsync(CancellationToken ct);

    /// <summary>Notifications von unten (u. a. tools/list_changed des Upstreams selbst).</summary>
    event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived;
}

/// <summary>
/// Besitzt alle Upstream-Lebenszyklen (ADR-0005): Zustandsmaschine, Health-Loop, Restart mit Backoff,
/// Config-Versionierung. Add = Add→Connect→Discover→Katalog-Changed als eine Transaktion (DON'T Nr. 6).
/// </summary>
public interface IUpstreamSupervisor
{
    IReadOnlyList<UpstreamStatus> Statuses { get; }

    /// <summary>Wird bei Add/Remove/Zustands-/Inventarwechsel gefeuert. Handler müssen schnell und exception-frei sein.</summary>
    event EventHandler<UpstreamChangedEventArgs>? Changed;

    UpstreamStatus? GetStatus(ServerId id);

    /// <summary>Letztes Discovery-Ergebnis; null wenn unbekannt oder nie verbunden.</summary>
    UpstreamInventory? GetInventory(ServerId id);

    /// <summary>Aktive (guarded) Verbindung für das Routing; null wenn nicht Healthy/Degraded.</summary>
    IUpstreamConnection? GetConnection(ServerId id);

    Task<ServerId> AddAsync(UpstreamServerConfig config, CancellationToken ct);

    Task RemoveAsync(ServerId id, DrainPolicy drain, CancellationToken ct);

    Task SetEnabledAsync(ServerId id, bool enabled, CancellationToken ct);

    Task<ConfigVersionId> ReconfigureAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct);

    Task RollbackAsync(ServerId id, ConfigVersionId version, CancellationToken ct);
}
