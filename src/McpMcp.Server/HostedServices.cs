using McpMcp.Abstractions;
using McpMcp.Core.Upstreams;
using McpMcp.Persistence;
using McpMcp.Persistence.Audit;
using Microsoft.EntityFrameworkCore;

namespace McpMcp.Server;

/// <summary>
/// Startreihenfolge des Gateways (WP4.2): Schema sicherstellen → RBAC hydratisieren →
/// Bootstrap-Admin (nur bei leerer DB) → persistierte Upstreams wiederherstellen.
/// Beim Stop werden die Upstreams gedraint (ADR-0005).
/// </summary>
public sealed partial class GatewayStartupService : IHostedService
{
    private readonly IDbContextFactory<McpMcpDbContext> _factory;
    private readonly PersistentRbacStore _rbacStore;
    private readonly IApiKeyService _apiKeys;
    private readonly IUpstreamConfigStore _configStore;
    private readonly UpstreamSupervisor _supervisor;
    private readonly ILogger<GatewayStartupService> _logger;

    public GatewayStartupService(
        IDbContextFactory<McpMcpDbContext> factory,
        PersistentRbacStore rbacStore,
        IApiKeyService apiKeys,
        IUpstreamConfigStore configStore,
        UpstreamSupervisor supervisor,
        ILogger<GatewayStartupService> logger)
    {
        _factory = factory;
        _rbacStore = rbacStore;
        _apiKeys = apiKeys;
        _configStore = configStore;
        _supervisor = supervisor;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using (var db = await _factory.CreateDbContextAsync(cancellationToken))
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }

        await _rbacStore.LoadAsync(cancellationToken);
        await BootstrapAdminIfEmptyAsync(cancellationToken);

        var persisted = await _configStore.GetAllLatestAsync(cancellationToken);
        foreach (var (id, version) in persisted)
        {
            try
            {
                await _supervisor.RestoreAsync(id, version, cancellationToken);
            }
            catch (Exception ex)
            {
                Log.RestoreFailed(_logger, ex, version.Config.Slug);
            }
        }

        Log.Started(_logger, persisted.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await _supervisor.DisposeAsync();

    private async Task BootstrapAdminIfEmptyAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        if (await db.Identities.AnyAsync(ct))
        {
            return;
        }

        var role = new Role(RoleId.New(), "bootstrap-admin",
            [new Grant(new PermissionScope(null, null), [ToolAction.UseTool, ToolAction.ReadResource, ToolAction.UsePrompt])]);
        var identity = new Identity(IdentityId.New(), "bootstrap-admin", IdentityKind.Agent, [role.Id]);
        await _rbacStore.UpsertRoleAsync(role, ct);
        await _rbacStore.UpsertIdentityAsync(identity, ct);
        var key = await _apiKeys.IssueAsync(identity.Id, "bootstrap", expiresAt: null, ct);

        // Bewusste Ausnahme von DON'T Nr. 2: ohne diesen einmaligen Klartext-Key wäre eine
        // frische Instanz unbenutzbar (Henne-Ei). Der Key erscheint nur bei leerer DB, genau einmal.
        Log.BootstrapKey(_logger, key.PlaintextKey);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Gateway gestartet, {Count} persistierte Upstream-Server wiederhergestellt.")]
        public static partial void Started(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Restore von Upstream {Slug} fehlgeschlagen — Server bleibt inaktiv.")]
        public static partial void RestoreFailed(ILogger logger, Exception ex, string slug);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "ERSTSTART: Bootstrap-Admin angelegt. API-Key (wird NIE wieder angezeigt): {Key}")]
        public static partial void BootstrapKey(ILogger logger, string key);
    }
}

/// <summary>Betreibt den Audit-Batch-Writer; beim Stop wird der Channel vollständig gedraint (ADR-0007).</summary>
public sealed class AuditWriterService : BackgroundService
{
    private readonly AuditBatchWriter _writer;
    private readonly ChannelAuditSink _sink;

    public AuditWriterService(AuditBatchWriter writer, ChannelAuditSink sink)
    {
        _writer = writer;
        _sink = sink;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _writer.RunAsync(stoppingToken);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _sink.Complete();
        await base.StopAsync(cancellationToken);
    }
}

public sealed class AuditRetentionService : BackgroundService
{
    private readonly AuditRetentionJob _job;

    public AuditRetentionService(AuditRetentionJob job) => _job = job;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _job.RunAsync(stoppingToken);
}

/// <summary>Übersetzt Katalog-Änderungen (Server/Inventar/RBAC) in tools/list_changed an alle Sessions (FR-07).</summary>
public sealed class CatalogNotificationService : IHostedService
{
    private readonly IToolCatalog _catalog;
    private readonly McpSessionRegistry _registry;
    private EventHandler<CatalogChangedEventArgs>? _handler;

    public CatalogNotificationService(IToolCatalog catalog, McpSessionRegistry registry)
    {
        _catalog = catalog;
        _registry = registry;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _handler = (_, _) => _ = _registry.NotifyToolListChangedAsync(CancellationToken.None);
        _catalog.Changed += _handler;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_handler is not null)
        {
            _catalog.Changed -= _handler;
        }

        return Task.CompletedTask;
    }
}
