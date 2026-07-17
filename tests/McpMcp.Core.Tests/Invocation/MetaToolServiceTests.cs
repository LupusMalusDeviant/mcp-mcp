using FluentAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Invocation;
using Xunit;

namespace McpMcp.Core.Tests.Invocation;

/// <summary>WP4.3: Meta-Tools sind RBAC-konsistent mit tools/list und laufen über die Invoker-Pipeline.</summary>
public class MetaToolServiceTests
{
    private readonly InvokerTestWorld _w = new();

    private Task<ToolInvocationResult> ExecuteAsync(IdentityId caller, string metaTool, object args)
        => _w.MetaTools.ExecuteAsync(
            caller, CallOrigin.Mcp, metaTool,
            System.Text.Json.JsonSerializer.SerializeToElement(args), CancellationToken.None);

    [Fact]
    public async Task Search_returns_only_visible_tools()
    {
        var restricted = _w.RegisterAgent(
            new Grant(new PermissionScope(null, _w.Echo), [ToolAction.UseTool]));

        // "message" matcht die echo-Beschreibung, "Schema" die free-Beschreibung — beide Kandidaten treffen.
        var result = await ExecuteAsync(restricted, MetaToolService.SearchToolsName, new { query = "message Schema" });

        result.Status.Should().Be(InvocationStatus.Success);
        var names = result.Content!.Value.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        names.Should().Contain("srv__echo").And.NotContain("srv__free", "Sichtbarkeit folgt Berechtigung (FR-29)");
        _w.Audit.Events.Should().ContainSingle().Which.Tool.Should().Be("search_tools");
    }

    [Fact]
    public async Task Search_without_query_fails_validation()
    {
        var admin = _w.RegisterAdmin();

        var result = await ExecuteAsync(admin, MetaToolService.SearchToolsName, new { falsch = 1 });

        result.Status.Should().Be(InvocationStatus.ValidationFailed);
    }

    [Fact]
    public async Task Describe_returns_schema_for_visible_tool_only()
    {
        var restricted = _w.RegisterAgent(
            new Grant(new PermissionScope(null, _w.Echo), [ToolAction.UseTool]));

        var visible = await ExecuteAsync(restricted, MetaToolService.DescribeToolName, new { name = "srv__echo" });
        var invisible = await ExecuteAsync(restricted, MetaToolService.DescribeToolName, new { name = "srv__free" });
        var missing = await ExecuteAsync(restricted, MetaToolService.DescribeToolName, new { name = "srv__nix" });

        visible.Status.Should().Be(InvocationStatus.Success);
        visible.Content!.Value.GetProperty("inputSchema").GetProperty("required")[0].GetString().Should().Be("message");
        invisible.Status.Should().Be(InvocationStatus.ToolNotFound,
            "nicht erlaubte Tools sind von nicht existierenden ununterscheidbar");
        missing.Status.Should().Be(InvocationStatus.ToolNotFound);
    }

    [Fact]
    public async Task Invoke_routes_through_invoker_with_target_rbac()
    {
        var restricted = _w.RegisterAgent(
            new Grant(new PermissionScope(null, _w.Echo), [ToolAction.UseTool]));

        var allowed = await ExecuteAsync(restricted, MetaToolService.InvokeToolName,
            new { name = "srv__echo", arguments = new { message = "hi" } });
        var denied = await ExecuteAsync(restricted, MetaToolService.InvokeToolName,
            new { name = "srv__free", arguments = new { } });

        allowed.Status.Should().Be(InvocationStatus.Success);
        _w.Connection.LastToolName.Should().Be("echo");
        denied.Status.Should().Be(InvocationStatus.Denied, "Ziel-RBAC greift auch über invoke_tool");
        _w.Audit.Events.Should().HaveCount(2, "invoke_tool auditiert nur den inneren Ziel-Call — kein Doppel-Audit");
    }

    [Fact]
    public async Task Invoke_without_name_fails_validation_and_audits()
    {
        var admin = _w.RegisterAdmin();

        var result = await ExecuteAsync(admin, MetaToolService.InvokeToolName, new { arguments = new { } });

        result.Status.Should().Be(InvocationStatus.ValidationFailed);
        _w.Audit.Events.Should().ContainSingle().Which.Tool.Should().Be("invoke_tool");
    }

    [Fact]
    public void Definitions_expose_three_meta_tools_with_schemas()
    {
        MetaToolService.Definitions.Should().HaveCount(3);
        MetaToolService.Definitions.Select(d => d.Name).Should().BeEquivalentTo(
            "search_tools", "describe_tool", "invoke_tool");
        MetaToolService.Definitions.Should().OnlyContain(
            d => d.InputSchema.ValueKind == System.Text.Json.JsonValueKind.Object);
        MetaToolService.IsMetaTool("search_tools").Should().BeTrue();
        MetaToolService.IsMetaTool("srv__echo").Should().BeFalse();
    }
}
