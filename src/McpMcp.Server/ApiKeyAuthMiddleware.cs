using McpMcp.Abstractions;

namespace McpMcp.Server;

/// <summary>
/// API-Key-AuthN für den MCP-Endpoint (FR-27, WP4.4): Bearer-Token → Identität.
/// Fehlversuche werden auditiert (FR-22); Health-Endpoints bleiben anonym.
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    public const string IdentityItemKey = "McpMcp.IdentityId";

    private readonly RequestDelegate _next;

    public ApiKeyAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context, IApiKeyValidator validator, IAuditSink audit, TimeProvider time)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp"))
        {
            await _next(context);
            return;
        }

        var token = ExtractBearer(context.Request);
        var identity = token is null
            ? null
            : await validator.ValidateAsync(token, context.RequestAborted);

        if (identity is null)
        {
            audit.Record(new AuditEvent(
                time.GetUtcNow(), null, CallOrigin.Mcp, AuditEventKind.Authentication, null,
                null, InvocationStatus.Denied, null, null, null, null));
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(new { error = "API-Key fehlt, ist ungültig oder widerrufen." });
            return;
        }

        context.Items[IdentityItemKey] = identity.Value;
        await _next(context);
    }

    private static string? ExtractBearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }
}
