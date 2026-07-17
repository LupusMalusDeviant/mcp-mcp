using System.Text.Json;
using McpMcp.Abstractions;
using McpMcp.Core.Invocation;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpMcp.Server;

/// <summary>
/// MCP-Handler des Gateways (WP4.2): tools/list aus der Profil-Sicht, tools/call über die
/// Invoker-Pipeline (inkl. Meta-Tools), Resources/Prompts-Passthrough (FR-04).
/// Alle Sichtbarkeit kommt aus derselben RBAC-Quelle wie überall (DO Nr. 2).
/// </summary>
internal static class GatewayMcpHandlers
{
    public static ValueTask<ListToolsResult> ListToolsAsync(
        RequestContext<ListToolsRequestParams> ctx, CancellationToken ct)
    {
        var identity = RequireIdentity(ctx.Services!);
        var catalog = ctx.Services!.GetRequiredService<IToolCatalog>();
        var view = catalog.GetViewFor(identity);

        var tools = view.PinnedTools
            .Where(e => e.Kind is CatalogEntryKind.Tool)
            .Select(e => new Tool { Name = e.Name.Value, Description = e.Description, InputSchema = e.InputSchema })
            .ToList();

        if (view.LazyToolsEnabled)
        {
            tools.AddRange(MetaToolService.Definitions.Select(d => new Tool
            {
                Name = d.Name,
                Description = d.Description,
                InputSchema = d.InputSchema,
            }));
        }

        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
    }

    public static async ValueTask<CallToolResult> CallToolAsync(
        RequestContext<CallToolRequestParams> ctx, CancellationToken ct)
    {
        var identity = RequireIdentity(ctx.Services!);
        var name = ctx.Params?.Name ?? throw new McpException("tools/call ohne Tool-Namen.");
        var args = ToJsonElement(ctx.Params.Arguments);

        ToolInvocationResult result;
        if (MetaToolService.IsMetaTool(name))
        {
            var metaTools = ctx.Services!.GetRequiredService<MetaToolService>();
            result = await metaTools.ExecuteAsync(identity, CallOrigin.Mcp, name, args, ct).ConfigureAwait(false);
        }
        else
        {
            var invoker = ctx.Services!.GetRequiredService<IToolInvoker>();
            result = await invoker.InvokeAsync(
                new ToolInvocationRequest(identity, CallOrigin.Mcp, new NamespacedToolName(name), args, null), ct)
                .ConfigureAwait(false);
        }

        return ToCallToolResult(result);
    }

    public static ValueTask<ListResourcesResult> ListResourcesAsync(
        RequestContext<ListResourcesRequestParams> ctx, CancellationToken ct)
    {
        var identity = RequireIdentity(ctx.Services!);
        var (catalog, authorization, supervisor) = ResolveCatalogServices(ctx.Services!);

        var visible = authorization.FilterVisible(identity, catalog.Snapshot)
            .Where(e => e.Kind is CatalogEntryKind.Resource)
            .Select(e => e.Name)
            .ToHashSet();

        var resources = new List<Resource>();
        foreach (var status in supervisor.Statuses)
        {
            if (supervisor.GetInventory(status.Id) is not { } inventory)
            {
                continue;
            }

            resources.AddRange(inventory.Resources
                .Where(r => visible.Contains(NamespacedToolName.Create(status.Slug, r.Name)))
                .Select(r => new Resource
                {
                    Uri = r.Uri.ToString(),
                    Name = NamespacedToolName.Create(status.Slug, r.Name).Value,
                    Description = r.Description,
                    MimeType = r.MimeType,
                }));
        }

        return ValueTask.FromResult(new ListResourcesResult { Resources = resources });
    }

    public static async ValueTask<ReadResourceResult> ReadResourceAsync(
        RequestContext<ReadResourceRequestParams> ctx, CancellationToken ct)
    {
        var identity = RequireIdentity(ctx.Services!);
        var uri = ctx.Params?.Uri ?? throw new McpException("resources/read ohne URI.");
        var (_, authorization, supervisor) = ResolveCatalogServices(ctx.Services!);

        foreach (var status in supervisor.Statuses)
        {
            var resource = supervisor.GetInventory(status.Id)?.Resources
                .FirstOrDefault(r => string.Equals(r.Uri.ToString(), uri, StringComparison.Ordinal));
            if (resource is null)
            {
                continue;
            }

            var name = NamespacedToolName.Create(status.Slug, resource.Name);
            var decision = authorization.Evaluate(
                identity, new PermissionScope(status.Id, name), ToolAction.ReadResource);
            AuditPassthrough(ctx.Services!, identity, status.Id, name.Value,
                decision.Allowed ? InvocationStatus.Success : InvocationStatus.Denied);
            if (!decision.Allowed)
            {
                throw new McpException($"Resource '{uri}' ist nicht sichtbar.");
            }

            var connection = supervisor.GetConnection(status.Id)
                ?? throw new McpException($"Upstream für Resource '{uri}' ist nicht verbunden.");
            var payload = await connection.ReadResourceAsync(resource.Uri, ct).ConfigureAwait(false);
            return Deserialize<ReadResourceResult>(payload);
        }

        throw new McpException($"Resource '{uri}' existiert nicht.");
    }

    public static ValueTask<ListPromptsResult> ListPromptsAsync(
        RequestContext<ListPromptsRequestParams> ctx, CancellationToken ct)
    {
        var identity = RequireIdentity(ctx.Services!);
        var (catalog, authorization, _) = ResolveCatalogServices(ctx.Services!);

        var prompts = authorization.FilterVisible(identity, catalog.Snapshot)
            .Where(e => e.Kind is CatalogEntryKind.Prompt)
            .Select(e => new Prompt { Name = e.Name.Value, Description = e.Description })
            .ToList();

        return ValueTask.FromResult(new ListPromptsResult { Prompts = prompts });
    }

    public static async ValueTask<GetPromptResult> GetPromptAsync(
        RequestContext<GetPromptRequestParams> ctx, CancellationToken ct)
    {
        var identity = RequireIdentity(ctx.Services!);
        var name = ctx.Params?.Name ?? throw new McpException("prompts/get ohne Namen.");
        var (catalog, authorization, supervisor) = ResolveCatalogServices(ctx.Services!);

        var namespaced = new NamespacedToolName(name);
        var entry = catalog.Find(namespaced);
        var allowed = entry is not null
            && entry.Kind is CatalogEntryKind.Prompt
            && authorization.Evaluate(identity, new PermissionScope(entry.Server, entry.Name), ToolAction.UsePrompt).Allowed;
        AuditPassthrough(ctx.Services!, identity, entry?.Server, name,
            allowed ? InvocationStatus.Success : InvocationStatus.Denied);
        if (!allowed || !namespaced.TrySplit(out _, out var promptName))
        {
            throw new McpException($"Prompt '{name}' existiert nicht oder ist nicht sichtbar.");
        }

        var connection = supervisor.GetConnection(entry!.Server)
            ?? throw new McpException($"Upstream für Prompt '{name}' ist nicht verbunden.");
        var args = ctx.Params?.Arguments is { } dict ? JsonSerializer.SerializeToElement(dict) : (JsonElement?)null;
        var payload = await connection.GetPromptAsync(promptName, args, ct).ConfigureAwait(false);
        return Deserialize<GetPromptResult>(payload);
    }

    internal static CallToolResult ToCallToolResult(ToolInvocationResult result)
    {
        if (result.Status is not InvocationStatus.Success)
        {
            // DoD WP4: RBAC-Deny und andere Fehler als sauberer Tool-Error, nie als Protokoll-Absturz.
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"[{result.Status}] {result.ErrorMessage}" }],
            };
        }

        var content = result.Content!.Value;
        if (content.ValueKind is JsonValueKind.Object && content.TryGetProperty("content", out _))
        {
            // Upstream-Passthrough: das Ergebnis IST bereits ein serialisiertes CallToolResult.
            return Deserialize<CallToolResult>(content);
        }

        // Meta-Tool-Payloads (search/describe) als JSON-Text ausliefern.
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = content.GetRawText() }],
        };
    }

    internal static IdentityId RequireIdentity(IServiceProvider services)
    {
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext
            ?? throw new McpException("Kein HTTP-Kontext für die Session.");
        return http.Items.TryGetValue(ApiKeyAuthMiddleware.IdentityItemKey, out var value) && value is IdentityId id
            ? id
            : throw new McpException("Session ist nicht authentifiziert.");
    }

    private static (IToolCatalog Catalog, IAuthorizationService Authorization, IUpstreamSupervisor Supervisor)
        ResolveCatalogServices(IServiceProvider services)
        => (services.GetRequiredService<IToolCatalog>(),
            services.GetRequiredService<IAuthorizationService>(),
            services.GetRequiredService<IUpstreamSupervisor>());

    private static void AuditPassthrough(
        IServiceProvider services, IdentityId identity, ServerId? server, string name, InvocationStatus status)
        => services.GetRequiredService<IAuditSink>().Record(new AuditEvent(
            services.GetRequiredService<TimeProvider>().GetUtcNow(),
            identity, CallOrigin.Mcp, AuditEventKind.ToolCall, server, name, status, null, null, null, null));

    private static T Deserialize<T>(JsonElement element)
        => JsonSerializer.Deserialize<T>(element, McpJsonUtilities.DefaultOptions)
            ?? throw new McpException("Upstream lieferte ein nicht deserialisierbares Ergebnis.");

    private static JsonElement ToJsonElement(IDictionary<string, JsonElement>? arguments)
        => arguments is null ? default : JsonSerializer.SerializeToElement(arguments);
}
