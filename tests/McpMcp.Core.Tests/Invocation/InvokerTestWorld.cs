using System.Text.Json;
using McpMcp.Abstractions;
using McpMcp.Core.Audit;
using McpMcp.Core.Catalog;
using McpMcp.Core.Invocation;
using McpMcp.Core.Rbac;
using McpMcp.Core.Tests.Catalog;
using McpMcp.Core.Tests.Upstreams;
using Microsoft.Extensions.Time.Testing;

namespace McpMcp.Core.Tests.Invocation;

internal sealed class FakeAuditSink : IAuditSink
{
    private readonly List<AuditEvent> _events = [];

    public IReadOnlyList<AuditEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public void Record(AuditEvent evt)
    {
        lock (_events)
        {
            _events.Add(evt);
        }
    }
}

internal sealed class FakeRateLimiter : IRateLimiter
{
    public bool Allow { get; set; } = true;

    public bool TryAcquire(IdentityId identity) => Allow;
}

/// <summary>Komplette Invoker-Welt: echter Katalog/RBAC/Redaction, fake Supervisor/Audit/RateLimit.</summary>
internal sealed class InvokerTestWorld
{
    public FakeTimeProvider Time { get; } = new();

    public FakeSupervisor Supervisor { get; } = new();

    public InMemoryRbacDirectory Directory { get; } = new();

    public AuthorizationService Authorization { get; }

    public ToolCatalog Catalog { get; }

    public FakeAuditSink Audit { get; } = new();

    public FakeRateLimiter RateLimiter { get; } = new();

    public RedactionService Redaction { get; } = new();

    public ToolInvoker Invoker { get; }

    public MetaToolService MetaTools { get; }

    public ServerId Server { get; }

    public FakeUpstreamConnection Connection { get; }

    public InvokerTestWorld()
    {
        Authorization = new AuthorizationService(Directory);
        Server = Supervisor.SetServer("srv", new UpstreamInventory(
            [
                new ToolDescriptor("echo", "Echoes a message back.", StrictSchema()),
                new ToolDescriptor("free", "Tool ohne verwertbares Schema.", BrokenSchema()),
            ],
            [], []));
        Connection = new FakeUpstreamConnection { Id = Server };
        Supervisor.SetConnection(Server, Connection);
        Catalog = new ToolCatalog(Supervisor, Authorization, Directory);
        Invoker = new ToolInvoker(Authorization, RateLimiter, Catalog, Supervisor, Audit, Redaction, Time);
        MetaTools = new MetaToolService(Catalog, Authorization, Invoker, Audit, Time);
    }

    public NamespacedToolName Echo { get; } = new("srv__echo");

    public NamespacedToolName Free { get; } = new("srv__free");

    public IdentityId RegisterAgent(params Grant[] grants)
    {
        var role = new Role(RoleId.New(), "rolle", grants);
        Directory.UpsertRole(role);
        var id = IdentityId.New();
        Directory.UpsertIdentity(new Identity(id, "agent", IdentityKind.Agent, [role.Id]));
        return id;
    }

    public IdentityId RegisterAdmin()
        => RegisterAgent(new Grant(new PermissionScope(null, null), [ToolAction.UseTool, ToolAction.ReadResource, ToolAction.UsePrompt]));

    public static ToolInvocationRequest Request(
        IdentityId caller, NamespacedToolName tool, object? args = null, TimeSpan? timeoutOverride = null)
        => new(
            caller,
            CallOrigin.Mcp,
            tool,
            args is null ? default : JsonSerializer.SerializeToElement(args),
            timeoutOverride);

    private static JsonElement StrictSchema()
    {
        using var doc = JsonDocument.Parse(
            """{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}""");
        return doc.RootElement.Clone();
    }

    private static JsonElement BrokenSchema()
    {
        // Syntaktisch JSON, aber kein auswertbares Schema (type-Wert ist Unsinn) — R3-Fallback-Pfad.
        using var doc = JsonDocument.Parse("""{"type":12345}""");
        return doc.RootElement.Clone();
    }
}
