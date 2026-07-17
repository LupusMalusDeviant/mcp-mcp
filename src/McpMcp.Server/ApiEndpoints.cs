using System.Text.Json;
using McpMcp.Abstractions;
using McpMcp.Core.Upstreams;
using McpMcp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace McpMcp.Server;

/// <summary>
/// REST-Fassade (FR-17) + Management-API (WP5.1, Basis für UI-Parität/FR-41).
/// Beide laufen durch dieselben Kernpfade wie MCP: Invoker-Pipeline bzw. Application-Services
/// (ADR-0008 — kein doppelter Enforcement-Code). Management verlangt bis WP6 einen Global-Grant.
/// </summary>
internal static class ApiEndpoints
{
    public static void MapGatewayApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        // ── Tool-Fassade (jede authentifizierte Identität, RBAC filtert) ─────
        api.MapGet("/tools", (HttpContext ctx, IToolCatalog catalog, IAuthorizationService auth) =>
        {
            var identity = Identity(ctx);
            var tools = auth.FilterVisible(identity, catalog.Snapshot)
                .Where(e => e.Kind is CatalogEntryKind.Tool)
                .Select(e => new
                {
                    name = e.Name.Value,
                    description = e.Description,
                    inputSchema = e.InputSchema,
                    estimatedSchemaTokens = e.EstimatedSchemaTokens,
                });
            return Results.Ok(new { tools });
        });

        api.MapPost("/tools/{name}/invoke", async (
            string name, HttpContext ctx, IToolInvoker invoker, CancellationToken ct) =>
        {
            JsonElement args = default;
            if (ctx.Request.ContentLength > 0)
            {
                args = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ct);
            }

            var result = await invoker.InvokeAsync(
                new ToolInvocationRequest(Identity(ctx), CallOrigin.Rest, new NamespacedToolName(name), args, null), ct);

            return result.Status switch
            {
                InvocationStatus.Success => Results.Ok(new { status = "Success", content = result.Content }),
                InvocationStatus.ValidationFailed => Error(StatusCodes.Status400BadRequest, result),
                InvocationStatus.Denied => Error(StatusCodes.Status403Forbidden, result),
                InvocationStatus.ToolNotFound => Error(StatusCodes.Status404NotFound, result),
                InvocationStatus.Timeout => Error(StatusCodes.Status504GatewayTimeout, result),
                _ => Error(StatusCodes.Status502BadGateway, result),
            };
        });

        api.MapGet("/openapi.json", (HttpContext ctx, OpenApiDocumentGenerator generator) =>
            Results.Text(generator.GetJsonFor(Identity(ctx)), "application/json"));

        // ── Management: Upstream-Server (FR-34-Basis) ────────────────────────
        var servers = api.MapGroup("/servers").AddEndpointFilter(RequireAdminAsync);

        servers.MapGet("/", (IUpstreamSupervisor supervisor) => Results.Ok(new
        {
            servers = supervisor.Statuses.Select(s => new
            {
                id = s.Id.Value,
                slug = s.Slug,
                state = s.State.ToString(),
                toolCount = s.ToolCount,
                lastError = s.LastError,
                lastHealthyAt = s.LastHealthyAt,
            }),
        }));

        servers.MapPost("/", async (
            UpstreamServerConfig config, HttpContext ctx, UpstreamSupervisor supervisor, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            try
            {
                var id = await supervisor.AddAsync(config, ct);
                AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, id, $"server-added:{config.Slug}");
                return Results.Created($"/api/v1/servers/{id.Value}", new { id = id.Value });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        servers.MapDelete("/{id:guid}", async (
            Guid id, int? graceSeconds, HttpContext ctx, UpstreamSupervisor supervisor, IAuditSink audit,
            TimeProvider time, CancellationToken ct) =>
        {
            try
            {
                await supervisor.RemoveAsync(
                    new ServerId(id),
                    graceSeconds is { } g ? DrainPolicy.Graceful(TimeSpan.FromSeconds(g)) : DrainPolicy.Immediate,
                    ct);
                AuditManagement(audit, time, ctx, AuditEventKind.ConfigChanged, new ServerId(id), "server-removed");
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        servers.MapPost("/{id:guid}/enabled", async (
            Guid id, EnabledRequest body, UpstreamSupervisor supervisor, CancellationToken ct) =>
        {
            try
            {
                await supervisor.SetEnabledAsync(new ServerId(id), body.Enabled, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        servers.MapPut("/{id:guid}", async (
            Guid id, UpstreamServerConfig config, UpstreamSupervisor supervisor, CancellationToken ct) =>
        {
            try
            {
                var version = await supervisor.ReconfigureAsync(new ServerId(id), config, ct);
                return Results.Ok(new { version = version.Value });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        servers.MapPost("/{id:guid}/rollback", async (
            Guid id, RollbackRequest body, UpstreamSupervisor supervisor, CancellationToken ct) =>
        {
            try
            {
                await supervisor.RollbackAsync(new ServerId(id), new ConfigVersionId(body.Version), ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        servers.MapGet("/{id:guid}/history", async (
            Guid id, IUpstreamConfigStore store, CancellationToken ct) =>
        {
            var history = await store.GetHistoryAsync(new ServerId(id), ct);
            return Results.Ok(new
            {
                versions = history.Select(v => new
                {
                    version = v.Version.Value,
                    savedAt = v.SavedAt,
                    config = RedactConfig(v.Config),
                }),
            });
        });

        // ── Management: RBAC (FR-36-Basis) ───────────────────────────────────
        var rbac = api.MapGroup("/rbac").AddEndpointFilter(RequireAdminAsync);

        rbac.MapGet("/identities", async (IDbContextFactory<McpMcpDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await db.Identities.AsNoTracking().ToListAsync(ct);
            return Results.Ok(new { identities = rows });
        });

        rbac.MapPost("/identities", async (
            Identity identity, PersistentRbacStore store, IAuditSink audit, TimeProvider time, HttpContext ctx,
            CancellationToken ct) =>
        {
            await store.UpsertIdentityAsync(identity, ct);
            AuditManagement(audit, time, ctx, AuditEventKind.RbacChanged, null, $"identity:{identity.Name}");
            return Results.Ok(new { id = identity.Id.Value });
        });

        rbac.MapDelete("/identities/{id:guid}", async (Guid id, PersistentRbacStore store, CancellationToken ct) =>
        {
            await store.RemoveIdentityAsync(new IdentityId(id), ct);
            return Results.NoContent();
        });

        rbac.MapGet("/roles", async (IDbContextFactory<McpMcpDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok(new { roles = await db.Roles.AsNoTracking().ToListAsync(ct) });
        });

        rbac.MapPost("/roles", async (Role role, PersistentRbacStore store, CancellationToken ct) =>
        {
            await store.UpsertRoleAsync(role, ct);
            return Results.Ok(new { id = role.Id.Value });
        });

        rbac.MapDelete("/roles/{id:guid}", async (Guid id, PersistentRbacStore store, CancellationToken ct) =>
        {
            await store.RemoveRoleAsync(new RoleId(id), ct);
            return Results.NoContent();
        });

        rbac.MapGet("/profiles", async (IDbContextFactory<McpMcpDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok(new { profiles = await db.Profiles.AsNoTracking().ToListAsync(ct) });
        });

        rbac.MapPost("/profiles", async (ToolProfile profile, PersistentRbacStore store, CancellationToken ct) =>
        {
            await store.UpsertProfileAsync(profile, ct);
            return Results.Ok(new { id = profile.Id.Value });
        });

        rbac.MapDelete("/profiles/{id:guid}", async (Guid id, PersistentRbacStore store, CancellationToken ct) =>
        {
            await store.RemoveProfileAsync(new ProfileId(id), ct);
            return Results.NoContent();
        });

        rbac.MapPost("/identities/{id:guid}/keys", async (
            Guid id, IssueKeyRequest body, IApiKeyService keys, CancellationToken ct) =>
        {
            var issued = await keys.IssueAsync(new IdentityId(id), body.Label, body.ExpiresAt, ct);
            return Results.Ok(new
            {
                keyId = issued.KeyId,
                plaintextKey = issued.PlaintextKey,
                warnung = "Dieser Key wird nie wieder angezeigt.",
            });
        });

        rbac.MapGet("/keys", async (Guid? identityId, IApiKeyService keys, CancellationToken ct) =>
        {
            var list = await keys.ListAsync(identityId is { } i ? new IdentityId(i) : null, ct);
            return Results.Ok(new { keys = list });
        });

        rbac.MapDelete("/keys/{keyId:guid}", async (Guid keyId, IApiKeyService keys, CancellationToken ct) =>
        {
            await keys.RevokeAsync(keyId, ct);
            return Results.NoContent();
        });

        // ── Management: Audit-Log (FR-23-Basis) ──────────────────────────────
        api.MapGet("/audit", async (
            HttpContext ctx, IAuditQuery query,
            DateTimeOffset? from, DateTimeOffset? to, Guid? caller, Guid? server, string? tool,
            InvocationStatus? status, AuditEventKind? kind, int page, int pageSize,
            CancellationToken ct) =>
        {
            var result = await query.QueryAsync(
                new AuditFilter(
                    from, to,
                    caller is { } c ? new IdentityId(c) : null,
                    server is { } s ? new ServerId(s) : null,
                    tool, status, kind,
                    page < 1 ? 1 : page,
                    pageSize < 1 ? 100 : Math.Min(pageSize, 1000)),
                ct);
            return Results.Ok(result);
        }).AddEndpointFilter(RequireAdminAsync);
    }

    private static IdentityId Identity(HttpContext ctx) => (IdentityId)ctx.Items[ApiKeyAuthMiddleware.IdentityItemKey]!;

    /// <summary>Bis WP6 echte UI-Rollen bringt: Management verlangt einen Global-Grant (Plan-Änderungslog WP5).</summary>
    private static async ValueTask<object?> RequireAdminAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var auth = context.HttpContext.RequestServices.GetRequiredService<IAuthorizationService>();
        var decision = auth.Evaluate(Identity(context.HttpContext), new PermissionScope(null, null), ToolAction.UseTool);
        return decision.Allowed
            ? await next(context)
            : Results.Json(
                new { error = "Management-API erfordert eine Identität mit Global-Grant." },
                statusCode: StatusCodes.Status403Forbidden);
    }

    private static IResult Error(int statusCode, ToolInvocationResult result)
        => Results.Json(new { status = result.Status.ToString(), error = result.ErrorMessage }, statusCode: statusCode);

    /// <summary>DON'T Nr. 2: Secrets tauchen auch in Admin-Antworten nicht auf.</summary>
    private static UpstreamServerConfig RedactConfig(UpstreamServerConfig config) => config with
    {
        Stdio = config.Stdio is { EnvironmentVariables: { Count: > 0 } env } stdio
            ? stdio with { EnvironmentVariables = env.ToDictionary(kv => kv.Key, _ => "***") }
            : config.Stdio,
        Http = config.Http is { Headers: { Count: > 0 } headers } http
            ? http with { Headers = headers.ToDictionary(kv => kv.Key, _ => "***") }
            : config.Http,
        OpenApi = config.OpenApi is { Credential: not null } openApi
            ? openApi with { Credential = "***" }
            : config.OpenApi,
    };

    private static void AuditManagement(
        IAuditSink audit, TimeProvider time, HttpContext ctx, AuditEventKind kind, ServerId? server, string subject)
        => audit.Record(new AuditEvent(
            time.GetUtcNow(), Identity(ctx), CallOrigin.Rest, kind, server, subject, null, null, null, null, null));

    private sealed record EnabledRequest(bool Enabled);

    private sealed record RollbackRequest(int Version);

    private sealed record IssueKeyRequest(string Label, DateTimeOffset? ExpiresAt);
}
