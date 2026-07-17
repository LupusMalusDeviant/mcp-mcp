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

/// <summary>Fabrik pro Transporttyp. Implementierungen: Stdio, StreamableHttp, OpenApi (ADR-0005/0008).</summary>
public interface IUpstreamConnector
{
    UpstreamTransportKind Kind { get; }

    Task<IUpstreamConnection> ConnectAsync(UpstreamServerConfig config, CancellationToken ct);
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

    Task<ServerId> AddAsync(UpstreamServerConfig config, CancellationToken ct);

    Task RemoveAsync(ServerId id, DrainPolicy drain, CancellationToken ct);

    Task SetEnabledAsync(ServerId id, bool enabled, CancellationToken ct);

    Task<ConfigVersionId> ReconfigureAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct);

    Task RollbackAsync(ServerId id, ConfigVersionId version, CancellationToken ct);
}
