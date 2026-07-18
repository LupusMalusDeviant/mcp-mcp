using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using McpMcp.Abstractions;
using Xunit;

namespace McpMcp.Integration.Tests.Gateway;

/// <summary>WP5.1/5.2-DoD: REST-Roundtrip, Fehler-Mapping, Audit-Parität MCP↔REST, OpenAPI-Sicht, Management.</summary>
public sealed class RestFacadeTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _gw;

    public RestFacadeTests(GatewayFixture gw) => _gw = gw;

    private HttpClient CreateApiClient(string apiKey)
    {
        var client = _gw.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        return client;
    }

    [Fact]
    public async Task Rest_invoke_roundtrip_works_like_curl()
    {
        await _gw.AddEchoUpstreamAsync("rest1");
        var (_, apiKey) = await _gw.SeedAdminAsync("rest-admin");
        using var client = CreateApiClient(apiKey);

        var tools = await client.GetFromJsonAsync<JsonElement>("/api/v1/tools");
        tools.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .Should().Contain("rest1__echo");

        var response = await client.PostAsync(
            "/api/v1/tools/rest1__echo/invoke",
            new StringContent("""{"message":"per REST"}""", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("content").GetProperty("content")[0].GetProperty("text").GetString()
            .Should().Be("Echo: per REST", "WP5-DoD: curl-Roundtrip gegen EchoServer-Tool");
    }

    [Fact]
    public async Task Error_mapping_matches_plan()
    {
        await _gw.AddEchoUpstreamAsync("rest2");
        var (_, adminKey) = await _gw.SeedAdminAsync("mapper-admin");
        var (_, restrictedKey) = await _gw.SeedIdentityAsync("mapper-restricted", grants: []);

        using var admin = CreateApiClient(adminKey);
        using var restricted = CreateApiClient(restrictedKey);
        var validBody = new StringContent("""{"message":"x"}""", Encoding.UTF8, "application/json");

        (await restricted.PostAsync("/api/v1/tools/rest2__echo/invoke", validBody))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "Denied → 403");
        (await admin.PostAsync("/api/v1/tools/rest2__gibtsnicht/invoke", validBody))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "ToolNotFound → 404");
        (await admin.PostAsync(
            "/api/v1/tools/rest2__echo/invoke",
            new StringContent("""{"falsch":1}""", Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "ValidationFailed → 400");
    }

    [Fact]
    public async Task Identical_call_via_mcp_and_rest_yields_identical_audit_semantics()
    {
        await _gw.AddEchoUpstreamAsync("parity1");
        var (identity, apiKey) = await _gw.SeedAdminAsync("parity-agent");

        // ASCII-Payload: der MCP-Pfad escapet Nicht-ASCII bei der Re-Serialisierung (ä),
        // was RequestBytes kosmetisch verschieben würde — hier zählt die Semantik-Parität.
        await using (var mcpClient = await _gw.ConnectClientAsync(apiKey))
        {
            await mcpClient.CallToolAsync(
                "parity1__echo", new Dictionary<string, object?> { ["message"] = "paritaet" });
        }

        using var restClient = CreateApiClient(apiKey);
        await restClient.PostAsync(
            "/api/v1/tools/parity1__echo/invoke",
            new StringContent("""{"message":"paritaet"}""", Encoding.UTF8, "application/json"));

        IReadOnlyList<AuditEvent> events = [];
        await IntegrationSupport.WaitUntilAsync(() =>
        {
            events = _gw.AuditQuery.QueryAsync(
                new AuditFilter(Caller: identity, ToolPrefix: "parity1__echo"), CancellationToken.None)
                .GetAwaiter().GetResult().Items;
            return events.Count == 2;
        });

        var viaMcp = events.Single(e => e.Origin == CallOrigin.Mcp);
        var viaRest = events.Single(e => e.Origin == CallOrigin.Rest);
        viaRest.Should().BeEquivalentTo(viaMcp, options => options
                .Excluding(e => e.Origin)
                .Excluding(e => e.Timestamp)
                .Excluding(e => e.Duration)
                .Excluding(e => e.RedactedArguments),
            "WP5-DoD: identische Audit-Semantik — nur Origin/Zeit/Dauer dürfen abweichen (ADR-0008)");
        viaRest.RedactedArguments!.Value.GetRawText().Should().Be(
            viaMcp.RedactedArguments!.Value.GetRawText(),
            "JsonElement hat keine strukturelle Gleichheit — Textvergleich der redigierten Argumente");
    }

    [Fact]
    public async Task OpenApi_document_reflects_only_the_callers_visible_tools()
    {
        await _gw.AddEchoUpstreamAsync("spec1");
        await _gw.AddEchoUpstreamAsync("spec2");
        var (_, restrictedKey) = await _gw.SeedIdentityAsync("spec-restricted",
            [new Grant(new PermissionScope(null, new NamespacedToolName("spec1__echo")), [ToolAction.UseTool])]);

        using var client = CreateApiClient(restrictedKey);
        var doc = await client.GetFromJsonAsync<JsonElement>("/api/v1/openapi.json");

        doc.GetProperty("openapi").GetString().Should().Be("3.1.0");
        var paths = doc.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        paths.Should().Contain("/api/v1/tools/spec1__echo/invoke")
            .And.NotContain("/api/v1/tools/spec2__echo/invoke", "FR-18: Spec ist RBAC-gefiltert pro Key");
    }

    [Fact]
    public async Task Management_requires_global_grant_and_can_add_servers()
    {
        var (_, adminKey) = await _gw.SeedAdminAsync("mgmt-admin");
        var (_, restrictedKey) = await _gw.SeedIdentityAsync("mgmt-restricted", grants: []);

        using var restricted = CreateApiClient(restrictedKey);
        (await restricted.GetAsync("/api/v1/servers")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "ohne Global-Grant kein Management");

        using var admin = CreateApiClient(adminKey);
        var add = await admin.PostAsJsonAsync("/api/v1/servers", new
        {
            slug = "mgmt1",
            displayName = "Per API angelegt",
            kind = "Stdio",
            enabled = true,
            stdio = new { command = TestPaths.Executable("EchoServer"), arguments = Array.Empty<string>() },
        });
        add.StatusCode.Should().Be(HttpStatusCode.Created);

        await IntegrationSupport.WaitUntilAsync(() =>
            _gw.Supervisor.Statuses.Any(s => s.Slug == "mgmt1" && s.State == UpstreamState.Healthy));

        var invoke = await admin.PostAsync(
            "/api/v1/tools/mgmt1__echo/invoke",
            new StringContent("""{"message":"per Management-API angelegt"}""", Encoding.UTF8, "application/json"));
        invoke.StatusCode.Should().Be(HttpStatusCode.OK, "der per API angelegte Server ist sofort nutzbar (FR-06)");

        var duplicate = await admin.PostAsJsonAsync("/api/v1/servers", new
        {
            slug = "mgmt1",
            displayName = "Doppelt",
            kind = "Stdio",
            enabled = true,
            stdio = new { command = "egal", arguments = Array.Empty<string>() },
        });
        duplicate.StatusCode.Should().Be(HttpStatusCode.BadRequest, "Slug-Kollision → verständlicher 400");
    }
}
