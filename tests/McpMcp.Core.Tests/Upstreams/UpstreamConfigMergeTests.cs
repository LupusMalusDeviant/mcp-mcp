using AwesomeAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Upstreams;
using Xunit;

namespace McpMcp.Core.Tests.Upstreams;

/// <summary>
/// FR-34: Beim Bearbeiten zeigt die UI bestehende Secrets nicht an. Ohne Übernahme würde jedes
/// Speichern sie stillschweigend löschen — ein Datenverlust, den niemand bemerkt, bis der
/// Upstream sich nicht mehr authentifizieren kann.
/// </summary>
public class UpstreamConfigMergeTests
{
    private static UpstreamServerConfig Stdio(IReadOnlyDictionary<string, string>? env) => new(
        "srv", "Server", UpstreamTransportKind.Stdio, Enabled: true,
        Stdio: new StdioTransportOptions("cmd", ["--x"], env));

    [Fact]
    public void Empty_env_keeps_the_previous_secrets()
    {
        var previous = Stdio(new Dictionary<string, string> { ["TOKEN"] = "geheim" });
        var edited = Stdio(null);

        var merged = UpstreamConfigMerge.CarryOverSecrets(edited, previous);

        merged.Stdio!.EnvironmentVariables.Should().ContainKey("TOKEN")
            .WhoseValue.Should().Be("geheim", "leere Secret-Felder bedeuten 'unverändert'");
    }

    [Fact]
    public void Provided_env_replaces_the_previous_secrets()
    {
        var previous = Stdio(new Dictionary<string, string> { ["TOKEN"] = "alt" });
        var edited = Stdio(new Dictionary<string, string> { ["TOKEN"] = "neu" });

        var merged = UpstreamConfigMerge.CarryOverSecrets(edited, previous);

        merged.Stdio!.EnvironmentVariables!["TOKEN"].Should().Be("neu",
            "wer etwas einträgt, will es auch ersetzen");
    }

    [Fact]
    public void Non_secret_changes_survive_the_merge()
    {
        var previous = Stdio(new Dictionary<string, string> { ["TOKEN"] = "geheim" });
        var edited = previous with { DisplayName = "Neuer Name", Stdio = previous.Stdio! with { EnvironmentVariables = null } };

        var merged = UpstreamConfigMerge.CarryOverSecrets(edited, previous);

        merged.DisplayName.Should().Be("Neuer Name");
        merged.Stdio!.EnvironmentVariables.Should().ContainKey("TOKEN");
    }

    [Fact]
    public void Http_headers_and_openapi_credentials_follow_the_same_rule()
    {
        var previousHttp = new UpstreamServerConfig(
            "h", "H", UpstreamTransportKind.StreamableHttp, true,
            Http: new HttpTransportOptions(
                new Uri("https://a.invalid/mcp"), new Dictionary<string, string> { ["Authorization"] = "Bearer x" }));
        var editedHttp = previousHttp with { Http = previousHttp.Http! with { Headers = null } };

        var previousApi = new UpstreamServerConfig(
            "a", "A", UpstreamTransportKind.OpenApi, true,
            OpenApi: new OpenApiTransportOptions(
                new Uri("https://a.invalid/spec.json"), AuthKind: OpenApiAuthKind.Bearer, Credential: "tok"));
        var editedApi = previousApi with { OpenApi = previousApi.OpenApi! with { Credential = null } };

        UpstreamConfigMerge.CarryOverSecrets(editedHttp, previousHttp)
            .Http!.Headers.Should().ContainKey("Authorization");
        UpstreamConfigMerge.CarryOverSecrets(editedApi, previousApi)
            .OpenApi!.Credential.Should().Be("tok");
    }
}
