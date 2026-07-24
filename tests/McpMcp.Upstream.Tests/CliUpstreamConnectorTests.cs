using System.Text.Json;
using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Upstream.Cli;
using Xunit;

namespace McpMcp.Upstream.Tests;

/// <summary>Smoke-Tests für den CLI-Upstream (ADR-0014). Nutzt das ohnehin vorhandene
/// <c>dotnet</c>-Binary als harmloses, portables Ziel-Programm.</summary>
public class CliUpstreamConnectorTests
{
    private static readonly JsonElement NoArgs = JsonSerializer.Deserialize<JsonElement>("{}");

    private static async Task<IUpstreamConnection> ConnectAsync(params CliToolSpec[] tools)
    {
        var config = new UpstreamServerConfig(
            "clitest", "CLI Test", UpstreamTransportKind.Cli, Enabled: true,
            Cli: new CliTransportOptions("dotnet", tools));
        return await new CliUpstreamConnector()
            .ConnectAsync(new ServerId(Guid.NewGuid()), config, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Discover_lists_the_configured_tools_with_an_args_schema()
    {
        await using var connection = await ConnectAsync(
            new CliToolSpec("version", "dotnet-Version", ["--version"]));

        var inventory = await connection.DiscoverAsync(TestContext.Current.CancellationToken);

        inventory.Tools.Should().ContainSingle();
        var tool = inventory.Tools[0];
        tool.Name.Should().Be("version");
        tool.Description.Should().Be("dotnet-Version");
        tool.InputSchema.GetProperty("properties").TryGetProperty("args", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Successful_command_returns_output_without_error_flag()
    {
        await using var connection = await ConnectAsync(
            new CliToolSpec("version", FixedArguments: ["--version"], AllowCallerArguments: false));

        var result = await connection.CallToolAsync("version", NoArgs, TestContext.Current.CancellationToken);

        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        text.Should().MatchRegex(@"\d+\.\d+"); // eine Versionsnummer wie 10.0.xxx
    }

    [Fact]
    public async Task Nonzero_exit_is_surfaced_as_isError()
    {
        await using var connection = await ConnectAsync(
            new CliToolSpec("bad", FixedArguments: ["--this-flag-does-not-exist"], AllowCallerArguments: false));

        var result = await connection.CallToolAsync("bad", NoArgs, TestContext.Current.CancellationToken);

        result.GetProperty("isError").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Unknown_tool_is_rejected()
    {
        await using var connection = await ConnectAsync(new CliToolSpec("version", FixedArguments: ["--version"]));

        var act = () => connection.CallToolAsync("does-not-exist", NoArgs, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
