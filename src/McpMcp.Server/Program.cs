using McpMcp.Abstractions;
using McpMcp.Core.Audit;
using McpMcp.Core.Catalog;
using McpMcp.Core.Invocation;
using McpMcp.Core.Rbac;
using McpMcp.Core.Upstreams;
using McpMcp.Persistence;
using McpMcp.Persistence.Audit;
using McpMcp.Server;
using McpMcp.Upstream;
using McpMcp.Web;
using McpMcp.Web.Components;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;

var builder = WebApplication.CreateBuilder(args);

// ── Konfiguration (NFR-05: Env-Vars + Volume) ────────────────────────────────
var dataDir = builder.Configuration["MCPMCP_DATA_DIR"] ?? "data";
Directory.CreateDirectory(dataDir);
var dbProvider = builder.Configuration["MCPMCP_DB_PROVIDER"] ?? "sqlite";
var connectionString = builder.Configuration["MCPMCP_DB_CONNECTION"]
    ?? $"Data Source={Path.Combine(dataDir, "mcpmcp.db")}";

// ── Persistenz & Schutz (ADR-0007, NFR-04) ───────────────────────────────────
builder.Services.AddDataProtection()
    .SetApplicationName("MCPMCP")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
builder.Services.AddDbContextFactory<McpMcpDbContext>(options =>
{
    if (string.Equals(dbProvider, "postgres", StringComparison.OrdinalIgnoreCase))
    {
        options.UseNpgsql(connectionString);
    }
    else
    {
        options.UseSqlite(connectionString);
    }
});
builder.Services.AddSingleton(new PersistenceOptions());
builder.Services.AddSingleton(TimeProvider.System);

// ── RBAC (ADR-0006) ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<InMemoryRbacDirectory>();
builder.Services.AddSingleton<IMutableRbacDirectory>(sp => sp.GetRequiredService<InMemoryRbacDirectory>());
builder.Services.AddSingleton<IRbacDirectory>(sp => sp.GetRequiredService<InMemoryRbacDirectory>());
builder.Services.AddSingleton<IAuthorizationService, AuthorizationService>();
builder.Services.AddSingleton<IRateLimiter, TokenBucketRateLimiter>();
builder.Services.AddSingleton<PersistentRbacStore>();
builder.Services.AddSingleton<IRbacManagement>(sp => sp.GetRequiredService<PersistentRbacStore>());
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddSingleton<IApiKeyService>(sp => sp.GetRequiredService<ApiKeyService>());
builder.Services.AddSingleton<IApiKeyValidator>(sp => sp.GetRequiredService<ApiKeyService>());
builder.Services.AddSingleton<IUiUserService, UiUserService>();
builder.Services.AddSingleton<IAssetStore, EfAssetStore>();
builder.Services.AddSingleton<McpMcp.Web.UiInternalIdentity>();

// ── Upstreams & Katalog (ADR-0005, WP2) ──────────────────────────────────────
builder.Services.AddSingleton<IUpstreamConnector, StdioUpstreamConnector>();
builder.Services.AddSingleton<IUpstreamConnector, StreamableHttpUpstreamConnector>();
builder.Services.AddSingleton<IUpstreamConnector, McpMcp.Upstream.OpenApi.OpenApiUpstreamConnector>();
builder.Services.AddSingleton<IUpstreamConfigStore, EfUpstreamConfigStore>();
builder.Services.AddSingleton<IUpstreamConnectionTester, UpstreamConnectionTester>();
builder.Services.AddSingleton(new SupervisorOptions());
builder.Services.AddSingleton<UpstreamSupervisor>(sp => new UpstreamSupervisor(
    sp.GetServices<IUpstreamConnector>(),
    sp.GetRequiredService<IUpstreamConfigStore>(),
    sp.GetRequiredService<SupervisorOptions>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<UpstreamSupervisor>>()));
builder.Services.AddSingleton<IUpstreamSupervisor>(sp => sp.GetRequiredService<UpstreamSupervisor>());
builder.Services.AddSingleton<ToolCatalog>(sp => new ToolCatalog(
    sp.GetRequiredService<IUpstreamSupervisor>(),
    sp.GetRequiredService<IAuthorizationService>(),
    sp.GetRequiredService<IRbacDirectory>(),
    sp.GetRequiredService<ILogger<ToolCatalog>>()));
builder.Services.AddSingleton<IToolCatalog>(sp => sp.GetRequiredService<ToolCatalog>());

// ── Audit (ADR-0007) & Invocation (ADR-0008) ─────────────────────────────────
builder.Services.AddSingleton<ChannelAuditSink>(sp =>
    new ChannelAuditSink(sp.GetRequiredService<PersistenceOptions>().AuditChannelCapacity));
builder.Services.AddSingleton<IAuditSink>(sp => sp.GetRequiredService<ChannelAuditSink>());
builder.Services.AddSingleton<AuditBatchWriter>();
builder.Services.AddSingleton<IAuditQuery, EfAuditQuery>();
builder.Services.AddSingleton<AuditRetentionJob>();
builder.Services.AddSingleton<RedactionService>();
builder.Services.AddSingleton<IRedactionService>(sp => sp.GetRequiredService<RedactionService>());
builder.Services.AddSingleton<IToolInvoker, ToolInvoker>();
builder.Services.AddSingleton<MetaToolService>();

// ── MCP-Endpoint (WP4.2) + REST-Fassade (WP5) ────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<McpSessionRegistry>();
builder.Services.AddSingleton<OpenApiDocumentGenerator>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "mcp-mcp", Version = "0.4.0" };
        options.ServerInstructions =
            "MCP-MCP gateway: aggregated tools from multiple upstream servers. " +
            "Use search_tools to discover capabilities, describe_tool for schemas, invoke_tool to call.";
    })
    .WithHttpTransport(options =>
    {
        // MCPEXP002: RunSessionHandler ist als experimentell markiert, aber der einzige
        // dokumentierte Hook für Session-Lifecycle (Registry für tools/list_changed, FR-07).
        // SDK ist per CPM auf 1.4.1 gepinnt — API-Drift trifft uns nur bei bewusstem Upgrade.
#pragma warning disable MCPEXP002
        options.RunSessionHandler = async (httpContext, server, ct) =>
        {
            var registry = httpContext.RequestServices.GetRequiredService<McpSessionRegistry>();
            var identity = (IdentityId)httpContext.Items[ApiKeyAuthMiddleware.IdentityItemKey]!;
            registry.Register(server, identity);
            try
            {
                await server.RunAsync(ct);
            }
            finally
            {
                registry.Unregister(server);
            }
        };
#pragma warning restore MCPEXP002
    })
    .WithListToolsHandler(GatewayMcpHandlers.ListToolsAsync)
    .WithCallToolHandler(GatewayMcpHandlers.CallToolAsync)
    .WithListResourcesHandler(GatewayMcpHandlers.ListResourcesAsync)
    .WithReadResourceHandler(GatewayMcpHandlers.ReadResourceAsync)
    .WithListPromptsHandler(GatewayMcpHandlers.ListPromptsAsync)
    .WithGetPromptHandler(GatewayMcpHandlers.GetPromptAsync);

// ── Web-UI (WP6, Blazor Interactive Server, ADR-0004) ────────────────────────
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAntiforgery();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.Name = "mcpmcp-ui";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(UiPolicies.Authenticated, p => p.RequireAuthenticatedUser())
    .AddPolicy(UiPolicies.Operator, p => p.RequireClaim(
        UiPolicies.RoleClaim, nameof(UiRole.Operator), nameof(UiRole.Admin)))
    .AddPolicy(UiPolicies.Admin, p => p.RequireClaim(
        UiPolicies.RoleClaim, nameof(UiRole.Admin)));

// ── Lifecycle ────────────────────────────────────────────────────────────────
builder.Services.AddHostedService<GatewayStartupService>();
builder.Services.AddHostedService<AuditWriterService>();
builder.Services.AddHostedService<AuditRetentionService>();
builder.Services.AddHostedService<CatalogNotificationService>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.UseMiddleware<ApiKeyAuthMiddleware>();
app.MapMcp("/mcp");
app.MapGatewayApi();
app.MapAuthEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/readyz", async (IDbContextFactory<McpMcpDbContext> factory, IUpstreamSupervisor supervisor, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    var dbOk = await db.Database.CanConnectAsync(ct);
    var statuses = supervisor.Statuses;
    return dbOk
        ? Results.Ok(new
        {
            status = "ready",
            upstreams = statuses.Select(s => new { s.Slug, state = s.State.ToString(), s.ToolCount }),
        })
        : Results.Json(new { status = "db-unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();

/// <summary>Marker für WebApplicationFactory-basierte Integrationstests.</summary>
public partial class Program
{
}
