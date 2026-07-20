using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Upstreams;
using Xunit;

namespace McpMcp.Core.Tests.Upstreams;

public class UpstreamConfigValidatorTests
{
    [Fact]
    public void Valid_stdio_config_passes()
    {
        var act = () => UpstreamConfigValidator.Validate(TestData.StdioConfig("github"));

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("UPPER")]
    [InlineData("bad__slug")]
    [InlineData("-leading-dash")]
    [InlineData("umlaut-ä")]
    public void Invalid_slugs_are_rejected(string slug)
    {
        var config = TestData.StdioConfig() with { Slug = slug };

        var act = () => UpstreamConfigValidator.Validate(config);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Stdio_kind_without_stdio_options_is_rejected()
    {
        var config = TestData.StdioConfig() with { Stdio = null };

        var act = () => UpstreamConfigValidator.Validate(config);

        act.Should().Throw<ArgumentException>().WithMessage("*Stdio*");
    }

    [Fact]
    public void Mismatched_extra_options_are_rejected()
    {
        var config = TestData.StdioConfig() with
        {
            Http = new HttpTransportOptions(new Uri("http://localhost:1234")),
        };

        var act = () => UpstreamConfigValidator.Validate(config);

        act.Should().Throw<ArgumentException>().WithMessage("*widersprüchlich*");
    }

    [Fact]
    public void Empty_stdio_command_is_rejected()
    {
        var config = TestData.StdioConfig() with
        {
            Stdio = new StdioTransportOptions("  ", []),
        };

        var act = () => UpstreamConfigValidator.Validate(config);

        act.Should().Throw<ArgumentException>().WithMessage("*Command*");
    }

    [Fact]
    public void NonPositive_call_timeout_is_rejected()
    {
        var config = TestData.StdioConfig() with { CallTimeout = TimeSpan.Zero };

        var act = () => UpstreamConfigValidator.Validate(config);

        act.Should().Throw<ArgumentException>().WithMessage("*CallTimeout*");
    }

    [Theory]
    [InlineData(-1, 1, 2.0, 10)]
    [InlineData(3, 0, 2.0, 10)]
    [InlineData(3, 1, 0.5, 10)]
    [InlineData(3, 5, 2.0, 1)]
    public void Invalid_restart_policies_are_rejected(int maxRetries, int initialSeconds, double multiplier, int maxSeconds)
    {
        var config = TestData.StdioConfig() with
        {
            Restart = new RestartPolicy(
                maxRetries, TimeSpan.FromSeconds(initialSeconds), multiplier, TimeSpan.FromSeconds(maxSeconds)),
        };

        var act = () => UpstreamConfigValidator.Validate(config);

        act.Should().Throw<ArgumentException>().WithMessage("*RestartPolicy*");
    }

    [Fact]
    public void Http_config_requires_http_options()
    {
        var config = new UpstreamServerConfig(
            "remote", "Remote", UpstreamTransportKind.StreamableHttp, Enabled: true);

        var act = () => UpstreamConfigValidator.Validate(config);

        act.Should().Throw<ArgumentException>().WithMessage("*Http*");
    }
}
