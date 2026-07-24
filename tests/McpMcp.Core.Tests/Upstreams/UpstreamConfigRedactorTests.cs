using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Upstreams;
using Xunit;

namespace McpMcp.Core.Tests.Upstreams;

public class UpstreamConfigRedactorTests
{
    [Fact]
    public void Every_transport_secret_is_masked_without_changing_keys()
    {
        var config = new UpstreamServerConfig(
            "cli", "CLI", UpstreamTransportKind.Cli, true,
            Cli: new CliTransportOptions(
                "tool",
                [new CliToolSpec("run")],
                EnvironmentVariables: new Dictionary<string, string>
                {
                    ["TOKEN"] = "cli-secret",
                    ["PASSWORD"] = "cli-password",
                }));

        var redacted = UpstreamConfigRedactor.Redact(config);

        redacted.Cli!.EnvironmentVariables.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["TOKEN"] = UpstreamConfigRedactor.Mask,
            ["PASSWORD"] = UpstreamConfigRedactor.Mask,
        });
        config.Cli!.EnvironmentVariables!["TOKEN"].Should().Be("cli-secret",
            "redaction must not mutate the persisted configuration");
    }
}
