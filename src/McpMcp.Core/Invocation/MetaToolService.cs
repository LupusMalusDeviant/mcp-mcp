using System.Diagnostics;
using System.Text.Json;
using McpMcp.Abstractions;

namespace McpMcp.Core.Invocation;

/// <summary>Definition eines eingebauten Meta-Tools für tools/list (Name, Beschreibung, Schema).</summary>
public sealed record MetaToolDefinition(string Name, string Description, JsonElement InputSchema);

/// <summary>
/// Die drei Meta-Tools des Lazy-Pfads (ADR-0003, FR-12): search_tools, describe_tool, invoke_tool.
/// Sichtbarkeit ist RBAC-konsistent mit tools/list (dieselbe FilterVisible-Quelle);
/// invoke_tool läuft durch den regulären <see cref="IToolInvoker"/> — Ziel-RBAC und Audit
/// greifen dort. search/describe werden hier selbst auditiert.
/// </summary>
public sealed class MetaToolService
{
    public const string SearchToolsName = "search_tools";
    public const string DescribeToolName = "describe_tool";
    public const string InvokeToolName = "invoke_tool";

    private const int DefaultSearchLimit = 10;
    private const int MaxSearchLimit = 50;

    public static IReadOnlyList<MetaToolDefinition> Definitions { get; } =
    [
        new(SearchToolsName,
            "Search the gateway's tool catalog by capability keywords. Returns compact matches without schemas; use describe_tool for the full input schema.",
            ParseSchema("""
                {"type":"object","properties":{
                  "query":{"type":"string","description":"Keywords describing the capability you need."},
                  "limit":{"type":"integer","minimum":1,"maximum":50,"description":"Maximum number of results (default 10)."}},
                 "required":["query"]}
                """)),
        new(DescribeToolName,
            "Get the full description and JSON input schema of one tool found via search_tools.",
            ParseSchema("""
                {"type":"object","properties":{
                  "name":{"type":"string","description":"Namespaced tool name, e.g. github__create_issue."}},
                 "required":["name"]}
                """)),
        new(InvokeToolName,
            "Invoke any permitted tool by its namespaced name with a JSON arguments object.",
            ParseSchema("""
                {"type":"object","properties":{
                  "name":{"type":"string","description":"Namespaced tool name, e.g. github__create_issue."},
                  "arguments":{"type":"object","description":"Arguments matching the tool's input schema."}},
                 "required":["name"]}
                """)),
    ];

    private readonly IToolCatalog _catalog;
    private readonly IAuthorizationService _authorization;
    private readonly IToolInvoker _invoker;
    private readonly IAuditSink _audit;
    private readonly TimeProvider _time;

    public MetaToolService(
        IToolCatalog catalog,
        IAuthorizationService authorization,
        IToolInvoker invoker,
        IAuditSink audit,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(audit);
        _catalog = catalog;
        _authorization = authorization;
        _invoker = invoker;
        _audit = audit;
        _time = timeProvider ?? TimeProvider.System;
    }

    public static bool IsMetaTool(string name)
        => name is SearchToolsName or DescribeToolName or InvokeToolName;

    public async Task<ToolInvocationResult> ExecuteAsync(
        IdentityId caller, CallOrigin origin, string metaTool, JsonElement args, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        switch (metaTool)
        {
            case SearchToolsName:
            {
                var result = SearchTools(caller, args, started);
                Audit(caller, origin, SearchToolsName, args, result);
                return result;
            }

            case DescribeToolName:
            {
                var result = DescribeTool(caller, args, started);
                Audit(caller, origin, DescribeToolName, args, result);
                return result;
            }

            case InvokeToolName:
            {
                // Ziel-RBAC + Audit übernimmt die Invoker-Pipeline — kein Doppel-Audit hier.
                return await InvokeToolAsync(caller, origin, args, started, ct).ConfigureAwait(false);
            }

            default:
                throw new ArgumentException($"'{metaTool}' ist kein Meta-Tool.", nameof(metaTool));
        }
    }

    private ToolInvocationResult SearchTools(IdentityId caller, JsonElement args, long started)
    {
        if (!TryGetString(args, "query", out var query))
        {
            return Fail(InvocationStatus.ValidationFailed, "search_tools erwartet ein 'query'-Argument (string).", started);
        }

        var limit = DefaultSearchLimit;
        if (args.ValueKind is JsonValueKind.Object
            && args.TryGetProperty("limit", out var limitProp)
            && limitProp.ValueKind is JsonValueKind.Number)
        {
            limit = Math.Clamp(limitProp.GetInt32(), 1, MaxSearchLimit);
        }

        var hits = _catalog.Search(caller, query, limit);
        var payload = JsonSerializer.SerializeToElement(new
        {
            tools = hits.Select(h => new { name = h.Name.Value, description = h.ShortDescription, score = h.Score }),
            hint = hits.Count > 0
                ? "Use describe_tool for the input schema, then invoke_tool to call it."
                : "No matching tools. Try broader keywords.",
        });
        return new ToolInvocationResult(InvocationStatus.Success, payload, null, Elapsed(started));
    }

    private ToolInvocationResult DescribeTool(IdentityId caller, JsonElement args, long started)
    {
        if (!TryGetString(args, "name", out var name))
        {
            return Fail(InvocationStatus.ValidationFailed, "describe_tool erwartet ein 'name'-Argument (string).", started);
        }

        var entry = _catalog.Find(new NamespacedToolName(name));
        if (entry is null || !_authorization
                .Evaluate(caller, new PermissionScope(entry.Server, entry.Name), ActionFor(entry.Kind)).Allowed)
        {
            // Sichtbarkeit folgt Berechtigung (FR-29): nicht erlaubte Tools sind auch hier unsichtbar.
            return Fail(InvocationStatus.ToolNotFound, $"Tool '{name}' existiert nicht oder ist nicht sichtbar.", started);
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            name = entry.Name.Value,
            description = entry.Description,
            inputSchema = entry.InputSchema,
            estimatedSchemaTokens = entry.EstimatedSchemaTokens,
        });
        return new ToolInvocationResult(InvocationStatus.Success, payload, null, Elapsed(started));
    }

    private async Task<ToolInvocationResult> InvokeToolAsync(
        IdentityId caller, CallOrigin origin, JsonElement args, long started, CancellationToken ct)
    {
        if (!TryGetString(args, "name", out var name))
        {
            var fail = Fail(InvocationStatus.ValidationFailed, "invoke_tool erwartet ein 'name'-Argument (string).", started);
            Audit(caller, origin, InvokeToolName, args, fail);
            return fail;
        }

        var arguments = args.ValueKind is JsonValueKind.Object && args.TryGetProperty("arguments", out var inner)
            ? inner
            : default;

        return await _invoker.InvokeAsync(
            new ToolInvocationRequest(caller, origin, new NamespacedToolName(name), arguments, null), ct)
            .ConfigureAwait(false);
    }

    private void Audit(IdentityId caller, CallOrigin origin, string metaTool, JsonElement args, ToolInvocationResult result)
        => _audit.Record(new AuditEvent(
            _time.GetUtcNow(), caller, origin, AuditEventKind.ToolCall, null, metaTool, result.Status,
            args.ValueKind is JsonValueKind.Undefined ? null : args,
            args.ValueKind is JsonValueKind.Undefined ? 0 : args.GetRawText().Length,
            result.Content?.GetRawText().Length,
            result.Duration));

    private static ToolAction ActionFor(CatalogEntryKind kind) => kind switch
    {
        CatalogEntryKind.Resource => ToolAction.ReadResource,
        CatalogEntryKind.Prompt => ToolAction.UsePrompt,
        _ => ToolAction.UseTool,
    };

    private static bool TryGetString(JsonElement args, string property, out string value)
    {
        value = string.Empty;
        if (args.ValueKind is JsonValueKind.Object
            && args.TryGetProperty(property, out var prop)
            && prop.ValueKind is JsonValueKind.String
            && prop.GetString() is { Length: > 0 } s)
        {
            value = s;
            return true;
        }

        return false;
    }

    private static ToolInvocationResult Fail(InvocationStatus status, string message, long started)
        => new(status, null, message, Elapsed(started));

    private static TimeSpan Elapsed(long started) => Stopwatch.GetElapsedTime(started);

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
