using McpMcp.Abstractions;
using McpMcp.Core.Upstreams;
using McpMcp.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace McpMcp.Integration.Tests.Gateway;

/// <summary>
/// Startet den echten Gateway-Host (Program.cs-Komposition) in-memory und stellt
/// SDK-Clients mit API-Key-AuthN bereit — die Grundlage aller WP4-DoD-Tests.
/// </summary>
public sealed class GatewayFixture : WebApplicationFactory<Program>
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"mcpmcp-e2e-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dataDir);
        builder.UseSetting("environment", "Development"); // Cookie SecurePolicy=SameAsRequest → Tests laufen über HTTP
        builder.UseSetting("MCPMCP_DATA_DIR", _dataDir);
        builder.UseSetting("MCPMCP_DB_CONNECTION", $"Data Source={Path.Combine(_dataDir, "e2e.db")}");
    }

    public UpstreamSupervisor Supervisor => Services.GetRequiredService<UpstreamSupervisor>();

    public PersistentRbacStore RbacStore => Services.GetRequiredService<PersistentRbacStore>();

    public IApiKeyService ApiKeys => Services.GetRequiredService<IApiKeyService>();

    public IAuditQuery AuditQuery => Services.GetRequiredService<IAuditQuery>();

    public IUiUserService UiUsers => Services.GetRequiredService<IUiUserService>();

    public IToolInvoker Invoker => Services.GetRequiredService<IToolInvoker>();

    /// <summary>HttpClient mit Cookie-Handling, der Redirects NICHT folgt (für Auth-/Authz-Prüfungen).</summary>
    public HttpClient CreateUiClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>Meldet einen UI-Nutzer per Form-POST an; der zurückgegebene Client trägt das Auth-Cookie.</summary>
    public async Task<HttpClient> LoginUiAsync(string username, string password)
    {
        var client = CreateUiClient();
        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["returnUrl"] = "/",
        }));
        if (response.StatusCode != System.Net.HttpStatusCode.Redirect
            || response.Headers.Location?.OriginalString == "/login?failed=true")
        {
            throw new InvalidOperationException($"UI-Login für '{username}' fehlgeschlagen ({response.StatusCode}).");
        }

        return client;
    }

    /// <summary>Identität + Rolle (+ optionales Profil) anlegen und einen API-Key ausstellen.</summary>
    public async Task<(IdentityId Identity, string ApiKey)> SeedIdentityAsync(
        string name, IReadOnlyList<Grant> grants, ToolProfile? profile = null)
    {
        if (profile is not null)
        {
            await RbacStore.UpsertProfileAsync(profile, CancellationToken.None);
        }

        var role = new Role(RoleId.New(), $"{name}-rolle", grants);
        await RbacStore.UpsertRoleAsync(role, CancellationToken.None);
        var identity = new Identity(IdentityId.New(), name, IdentityKind.Agent, [role.Id], profile?.Id);
        await RbacStore.UpsertIdentityAsync(identity, CancellationToken.None);
        var key = await ApiKeys.IssueAsync(identity.Id, $"{name}-key", null, CancellationToken.None);
        return (identity.Id, key.PlaintextKey);
    }

    public Task<(IdentityId Identity, string ApiKey)> SeedAdminAsync(string name = "e2e-admin", ToolProfile? profile = null)
        => SeedIdentityAsync(
            name,
            [new Grant(new PermissionScope(null, null), [ToolAction.UseTool, ToolAction.ReadResource, ToolAction.UsePrompt])],
            profile);

    /// <summary>Verbindet einen SDK-Client über den In-Memory-Host mit Bearer-AuthN.</summary>
    public async Task<McpClient> ConnectClientAsync(string apiKey)
    {
        var httpClient = CreateDefaultClient();
        httpClient.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = "e2e",
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
            },
            httpClient);
        return await McpClient.CreateAsync(transport);
    }

    public async Task<ServerId> AddEchoUpstreamAsync(string slug)
    {
        var id = await Supervisor.AddAsync(
            new UpstreamServerConfig(
                slug, $"Echo {slug}", UpstreamTransportKind.Stdio, Enabled: true,
                Stdio: new StdioTransportOptions(TestPaths.Executable("EchoServer"), [])),
            CancellationToken.None);
        await IntegrationSupport.WaitUntilAsync(
            () => Supervisor.GetStatus(id)?.State == UpstreamState.Healthy,
            because: $"EchoServer '{slug}' muss Healthy werden");
        return id;
    }
}
