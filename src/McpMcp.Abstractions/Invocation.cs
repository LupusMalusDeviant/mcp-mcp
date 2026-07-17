using System.Text.Json;

namespace McpMcp.Abstractions;

/// <summary>Herkunft eines Calls — alle drei Fassaden laufen durch denselben Invoker (ADR-0008).</summary>
public enum CallOrigin
{
    Mcp = 0,
    Rest = 1,
    Ui = 2,
}

public enum InvocationStatus
{
    Success = 0,
    UpstreamError = 1,
    Denied = 2,
    Timeout = 3,
    ValidationFailed = 4,
    ToolNotFound = 5,
}

public sealed record ToolInvocationRequest(
    IdentityId Caller,
    CallOrigin Origin,
    NamespacedToolName Tool,
    JsonElement Arguments,
    TimeSpan? TimeoutOverride);

public sealed record ToolInvocationResult(
    InvocationStatus Status,
    JsonElement? Content,
    string? ErrorMessage,
    TimeSpan Duration);

/// <summary>
/// Der EINZIGE Weg zu einem Tool-Call (DO Nr. 1): AuthN ist vorgelagert, die Pipeline übernimmt
/// RBAC-Check → Argument-Validierung → Routing → Timeout/Cancellation → Upstream-Call → Audit.
/// Wirft nicht bei fachlichen Fehlern — jedes Ergebnis ist ein <see cref="ToolInvocationResult"/> (DO Nr. 9).
/// </summary>
public interface IToolInvoker
{
    Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken ct);
}
