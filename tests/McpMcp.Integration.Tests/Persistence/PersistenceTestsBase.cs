using System.Text;
using System.Text.Json;
using FluentAssertions;
using McpMcp.Abstractions;
using McpMcp.Core.Audit;
using McpMcp.Core.Rbac;
using McpMcp.Persistence;
using McpMcp.Persistence.Audit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace McpMcp.Integration.Tests.Persistence;

internal sealed class TestDbContextFactory : IDbContextFactory<McpMcpDbContext>
{
    private readonly DbContextOptions<McpMcpDbContext> _options;

    public TestDbContextFactory(DbContextOptions<McpMcpDbContext> options) => _options = options;

    public McpMcpDbContext CreateDbContext() => new(_options);
}

/// <summary>
/// Provider-parametrisierte Persistenz-Tests (WP3-DoD): identische Suite läuft gegen
/// SQLite (Datei) und PostgreSQL (Testcontainer). Subklassen liefern die Optionen.
/// </summary>
public abstract class PersistenceTestsBase : IAsyncLifetime
{
    private const string Secret = "SUPERSECRET_VALUE_12345";

    internal IDbContextFactory<McpMcpDbContext> Factory { get; private set; } = null!;

    protected IDataProtectionProvider DataProtection { get; } = new EphemeralDataProtectionProvider();

    protected abstract Task<DbContextOptions<McpMcpDbContext>?> CreateOptionsAsync();

    /// <summary>Liefert eine zusätzliche, noch uninitialisierte Datenbank — für die Migrations-/Upgrade-Tests.</summary>
    protected abstract Task<DbContextOptions<McpMcpDbContext>> CreateFreshOptionsAsync(string name);

    protected abstract void MarkSkippedIfUnavailable();

    public async Task InitializeAsync()
    {
        var options = await CreateOptionsAsync();
        if (options is null)
        {
            return; // Provider nicht verfügbar — Tests skippen einzeln
        }

        Factory = new TestDbContextFactory(options);
        // Tests nutzen denselben Migrationspfad wie der Host (v1.1), nicht mehr EnsureCreated.
        await new DatabaseInitializer(Factory).InitializeAsync(CancellationToken.None);
    }

    public virtual Task DisposeAsync() => Task.CompletedTask;

    protected static UpstreamServerConfig ConfigWithSecret(string slug = "srv") => new(
        slug, $"Server {slug}", UpstreamTransportKind.Stdio, Enabled: true,
        Stdio: new StdioTransportOptions(
            "cmd", ["--arg"],
            new Dictionary<string, string> { ["API_TOKEN"] = Secret }));

    [SkippableFact]
    public async Task ConfigStore_roundtrips_versions_and_encrypts_payload()
    {
        MarkSkippedIfUnavailable();
        var store = new EfUpstreamConfigStore(Factory, DataProtection);
        var id = ServerId.New();

        var v1 = await store.AppendVersionAsync(id, ConfigWithSecret("alt"), CancellationToken.None);
        var v2 = await store.AppendVersionAsync(id, ConfigWithSecret("neu"), CancellationToken.None);

        v1.Value.Should().Be(1);
        v2.Value.Should().Be(2);
        (await store.GetVersionAsync(id, v1, CancellationToken.None))!.Slug.Should().Be("alt");
        (await store.GetVersionAsync(id, v2, CancellationToken.None))!.Stdio!.EnvironmentVariables!["API_TOKEN"]
            .Should().Be(Secret, "die Entschlüsselung muss verlustfrei sein");
        (await store.GetHistoryAsync(id, CancellationToken.None)).Select(h => h.Version.Value).Should().Equal(1, 2);

        // NFR-04: der persistierte Blob darf das Secret nicht im Klartext enthalten
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var payloads = await db.ConfigVersions.AsNoTracking().Select(r => r.Payload).ToListAsync();
            var secretBytes = Encoding.UTF8.GetBytes(Secret);
            foreach (var payload in payloads)
            {
                ContainsSubsequence(payload, secretBytes).Should().BeFalse("Config-Blobs sind verschlüsselt (NFR-04)");
            }
        }

        await store.RemoveAsync(id, CancellationToken.None);
        (await store.GetHistoryAsync(id, CancellationToken.None)).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task RbacStore_persists_and_rehydrates_directory()
    {
        MarkSkippedIfUnavailable();
        var writeDirectory = new InMemoryRbacDirectory();
        var store = new PersistentRbacStore(Factory, writeDirectory);

        var role = new Role(RoleId.New(), "reader",
            [new Grant(new PermissionScope(ServerId.New(), null), [ToolAction.UseTool, ToolAction.ReadResource])],
            new RateLimit(42));
        var profile = new ToolProfile(ProfileId.New(), "profil",
            [NamespacedToolName.Create("srv", "tool")], LazyToolsEnabled: true);
        var identity = new Identity(IdentityId.New(), "agent-x", IdentityKind.Agent, [role.Id], profile.Id);

        await store.UpsertRoleAsync(role, CancellationToken.None);
        await store.UpsertProfileAsync(profile, CancellationToken.None);
        await store.UpsertIdentityAsync(identity, CancellationToken.None);

        // Frisches Directory aus der DB hydratisieren — muss inhaltsgleich sein
        var freshDirectory = new InMemoryRbacDirectory();
        await new PersistentRbacStore(Factory, freshDirectory).LoadAsync(CancellationToken.None);

        freshDirectory.GetRole(role.Id).Should().BeEquivalentTo(role);
        freshDirectory.GetProfile(profile.Id).Should().BeEquivalentTo(profile);
        freshDirectory.GetIdentity(identity.Id).Should().BeEquivalentTo(identity);

        await store.RemoveIdentityAsync(identity.Id, CancellationToken.None);
        await store.RemoveRoleAsync(role.Id, CancellationToken.None);
        await store.RemoveProfileAsync(profile.Id, CancellationToken.None);
        var emptied = new InMemoryRbacDirectory();
        await new PersistentRbacStore(Factory, emptied).LoadAsync(CancellationToken.None);
        emptied.GetIdentity(identity.Id).Should().BeNull();
        emptied.GetRole(role.Id).Should().BeNull();
    }

    [SkippableFact]
    public async Task ApiKeys_issue_validate_revoke_and_expiry()
    {
        MarkSkippedIfUnavailable();
        var service = new ApiKeyService(Factory);
        var identity = IdentityId.New();

        var issued = await service.IssueAsync(identity, "test-key", expiresAt: null, CancellationToken.None);
        issued.PlaintextKey.Should().StartWith("mcpk_");

        (await service.ValidateAsync(issued.PlaintextKey, CancellationToken.None)).Should().Be(identity);
        (await service.ValidateAsync(issued.PlaintextKey + "x", CancellationToken.None)).Should().BeNull("manipuliertes Secret");
        (await service.ValidateAsync("mcpk_falschesformat", CancellationToken.None)).Should().BeNull();
        (await service.ValidateAsync("völlig-falsch", CancellationToken.None)).Should().BeNull();

        // Hash-Speicherung: Klartext-Secret darf nirgends in der Zeile stehen (NFR-04)
        var secretPart = issued.PlaintextKey.Split('_')[2];
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var row = await db.ApiKeys.AsNoTracking().SingleAsync(r => r.Id == issued.KeyId);
            row.Hash.Should().NotContain(secretPart);
        }

        var expired = await service.IssueAsync(identity, "abgelaufen", DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);
        (await service.ValidateAsync(expired.PlaintextKey, CancellationToken.None)).Should().BeNull("Gültigkeitsfenster (FR-31)");

        await service.RevokeAsync(issued.KeyId, CancellationToken.None);
        (await service.ValidateAsync(issued.PlaintextKey, CancellationToken.None)).Should().BeNull("Widerruf wirkt sofort");

        var list = await service.ListAsync(identity, CancellationToken.None);
        list.Should().HaveCount(2);
        list.Single(k => k.KeyId == issued.KeyId).RevokedAt.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task Audit_1000_mixed_calls_yield_exactly_1000_attributed_redacted_rows()
    {
        MarkSkippedIfUnavailable();
        var sink = new ChannelAuditSink();
        var writer = new AuditBatchWriter(sink, Factory, new PersistenceOptions
        {
            AuditFlushInterval = TimeSpan.FromMilliseconds(100),
            AuditMaxBatchSize = 200,
        });
        using var cts = new CancellationTokenSource();
        var run = writer.RunAsync(cts.Token);

        var redaction = new RedactionService();
        var tool = NamespacedToolName.Create("srv", "login");
        var identities = new[] { IdentityId.New(), IdentityId.New(), IdentityId.New() };
        var statuses = new[] { InvocationStatus.Success, InvocationStatus.Denied, InvocationStatus.UpstreamError, InvocationStatus.Timeout };
        var args = JsonSerializer.SerializeToElement(new { user = "anna", password = "streng-geheim" });

        for (var i = 0; i < 1000; i++)
        {
            sink.Record(new AuditEvent(
                DateTimeOffset.UtcNow,
                identities[i % identities.Length],
                CallOrigin.Mcp,
                AuditEventKind.ToolCall,
                ServerId.New(),
                tool.Value,
                statuses[i % statuses.Length],
                redaction.RedactArguments(tool, args),
                RequestBytes: 100,
                ResponseBytes: 200,
                Duration: TimeSpan.FromMilliseconds(5)));
        }

        await WaitForRowCountAsync(1000);
        cts.Cancel();
        await run;

        await using var db = await Factory.CreateDbContextAsync();
        (await db.AuditEvents.CountAsync()).Should().Be(1000, "PRD-Kriterium 5: exakt 1000 Zeilen für 1000 Calls");
        (await db.AuditEvents.CountAsync(r => r.CallerId == identities[0].Value)).Should().Be(334);
        (await db.AuditEvents.CountAsync(r => r.Status == (int)InvocationStatus.Denied)).Should().Be(250);
        sink.DroppedCount.Should().Be(0);

        var anyRow = await db.AuditEvents.AsNoTracking().FirstAsync();
        anyRow.RedactedArgumentsJson.Should().Contain("anna").And.NotContain("streng-geheim", "Secrets sind maskiert (FR-24)");
        anyRow.RedactedArgumentsJson.Should().Contain(RedactionService.Mask);
    }

    [SkippableFact]
    public async Task AuditQuery_filters_and_pages()
    {
        MarkSkippedIfUnavailable();
        var caller = IdentityId.New();
        var other = IdentityId.New();
        var baseTime = DateTimeOffset.UtcNow;
        await using (var db = await Factory.CreateDbContextAsync())
        {
            for (var i = 0; i < 30; i++)
            {
                db.AuditEvents.Add(new AuditEventRow
                {
                    Timestamp = baseTime.AddMinutes(-i),
                    CallerId = (i % 2 == 0 ? caller : other).Value,
                    Origin = (int)CallOrigin.Rest,
                    Kind = (int)AuditEventKind.ToolCall,
                    Tool = i < 10 ? "srv__a" : "srv__b",
                    Status = (int)(i % 3 == 0 ? InvocationStatus.Denied : InvocationStatus.Success),
                });
            }

            await db.SaveChangesAsync();
        }

        var query = new EfAuditQuery(Factory);

        var byCaller = await query.QueryAsync(new AuditFilter(Caller: caller), CancellationToken.None);
        byCaller.TotalCount.Should().Be(15);
        byCaller.Items.Should().OnlyContain(e => e.Caller == caller);

        var denied = await query.QueryAsync(new AuditFilter(Status: InvocationStatus.Denied), CancellationToken.None);
        denied.TotalCount.Should().Be(10);

        var byTool = await query.QueryAsync(new AuditFilter(Tool: "srv__a"), CancellationToken.None);
        byTool.TotalCount.Should().Be(10);

        var page2 = await query.QueryAsync(new AuditFilter(Page: 2, PageSize: 12), CancellationToken.None);
        page2.Items.Should().HaveCount(12);
        page2.TotalCount.Should().Be(30);

        var window = await query.QueryAsync(
            new AuditFilter(From: baseTime.AddMinutes(-9.5), To: baseTime), CancellationToken.None);
        window.TotalCount.Should().Be(10);
        window.Items.Should().BeInDescendingOrder(e => e.Timestamp);
    }

    [SkippableFact]
    public async Task Retention_removes_only_expired_events()
    {
        MarkSkippedIfUnavailable();
        var options = new PersistenceOptions { AuditRetention = TimeSpan.FromDays(7) };
        await using (var db = await Factory.CreateDbContextAsync())
        {
            db.AuditEvents.Add(new AuditEventRow { Timestamp = DateTimeOffset.UtcNow.AddDays(-30), Kind = 0, Origin = 0 });
            db.AuditEvents.Add(new AuditEventRow { Timestamp = DateTimeOffset.UtcNow.AddDays(-8), Kind = 0, Origin = 0 });
            db.AuditEvents.Add(new AuditEventRow { Timestamp = DateTimeOffset.UtcNow.AddDays(-1), Kind = 0, Origin = 0 });
            await db.SaveChangesAsync();
        }

        var deleted = await new AuditRetentionJob(Factory, options).ExecuteOnceAsync(CancellationToken.None);

        deleted.Should().Be(2);
        await using var check = await Factory.CreateDbContextAsync();
        (await check.AuditEvents.CountAsync()).Should().Be(1);
    }

    [SkippableFact]
    public async Task UiUsers_create_validate_and_enforce_unique_username()
    {
        MarkSkippedIfUnavailable();
        var service = new UiUserService(Factory);
        var name = $"betreiber-{Guid.NewGuid():N}";

        (await service.AnyExistAsync(CancellationToken.None)).Should().BeFalse();
        var created = await service.CreateAsync(name, "geheimes-passwort", UiRole.Operator, CancellationToken.None);
        created.Role.Should().Be(UiRole.Operator);
        (await service.AnyExistAsync(CancellationToken.None)).Should().BeTrue();

        (await service.ValidateCredentialsAsync(name, "geheimes-passwort", CancellationToken.None))!.Id
            .Should().Be(created.Id);
        (await service.ValidateCredentialsAsync(name, "falsch", CancellationToken.None)).Should().BeNull();
        (await service.ValidateCredentialsAsync("gibtsnicht", "x", CancellationToken.None)).Should().BeNull();

        // Passwort-Hash liegt nie im Klartext (NFR-04)
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var row = await db.UiUsers.AsNoTracking().SingleAsync(u => u.Username == name);
            row.PasswordHash.Should().NotContain("geheimes-passwort");
        }

        var act = () => service.CreateAsync(name, "andere", UiRole.Admin, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*existiert bereits*");

        await service.SetPasswordAsync(created.Id, "neues-passwort", CancellationToken.None);
        (await service.ValidateCredentialsAsync(name, "neues-passwort", CancellationToken.None)).Should().NotBeNull();
        (await service.ValidateCredentialsAsync(name, "geheimes-passwort", CancellationToken.None)).Should().BeNull();

        await service.DeleteAsync(created.Id, CancellationToken.None);
        (await service.ValidateCredentialsAsync(name, "neues-passwort", CancellationToken.None)).Should().BeNull();
    }

    [SkippableFact]
    public async Task Assets_version_and_retrieve()
    {
        MarkSkippedIfUnavailable();
        var store = new EfAssetStore(Factory);

        var id = await store.CreateAsync("mein-skill", "Ein Test-Skill", "Version-1-Inhalt", CancellationToken.None);
        var v2 = await store.PublishAsync(id, "Version-2-Inhalt", CancellationToken.None);
        v2.Value.Should().Be(2);

        (await store.GetAsync(id, null, CancellationToken.None)).Content.Should().Be("Version-2-Inhalt", "latest");
        (await store.GetAsync(id, new AssetVersion(1), CancellationToken.None)).Content.Should().Be("Version-1-Inhalt");

        var list = await store.ListAsync(IdentityId.New(), CancellationToken.None);
        list.Should().ContainSingle(a => a.Id == id).Which.LatestVersion.Value.Should().Be(2);
    }

    /// <summary>
    /// v1.1 hebt PBKDF2 von 100k auf 600k Iterationen. Bestehende Hashes tragen ihre Iterationszahl
    /// im Format mit und müssen weiterhin verifizieren — sonst würde das Upgrade alle Logins sperren.
    /// </summary>
    [SkippableFact]
    public async Task Password_hashed_with_legacy_iteration_count_still_verifies()
    {
        MarkSkippedIfUnavailable();
        const string password = "bestands-passwort";
        const int legacyIterations = 100_000;
        var username = $"altnutzer-{Guid.NewGuid():N}";

        // Hash im v1.0-Format (100k) von Hand erzeugen und direkt persistieren.
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, legacyIterations,
            System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        var legacyHash = $"{legacyIterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";

        await using (var db = await Factory.CreateDbContextAsync())
        {
            db.UiUsers.Add(new UiUserRow
            {
                Id = Guid.NewGuid(),
                Username = username,
                PasswordHash = legacyHash,
                Role = (int)UiRole.Admin,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var service = new UiUserService(Factory);
        (await service.ValidateCredentialsAsync(username, password, CancellationToken.None))
            .Should().NotBeNull("v1.0-Hashes müssen nach der Iterations-Erhöhung weiter funktionieren");
        (await service.ValidateCredentialsAsync(username, "falsch", CancellationToken.None))
            .Should().BeNull();
    }

    /// <summary>WP8.4: Die Recovery-Kommandos müssen aus dem „kein Zugang mehr"-Zustand herausführen.</summary>
    [SkippableFact]
    public async Task Recovery_commands_restore_ui_and_agent_access()
    {
        MarkSkippedIfUnavailable();
        var users = new UiUserService(Factory);
        var username = $"recovery-{Guid.NewGuid():N}";

        // Fall 1: Nutzer existiert nicht → wird als Admin angelegt.
        var created = await McpMcp.Server.AdminCommands.ResetUiAdminAsync(users, username, CancellationToken.None);
        created.WasExisting.Should().BeFalse();
        created.Role.Should().Be(UiRole.Admin);
        (await users.ValidateCredentialsAsync(username, created.Password, CancellationToken.None))
            .Should().NotBeNull("das ausgegebene Passwort muss funktionieren");

        // Fall 2: Nutzer existiert → Passwort neu, Rolle unverändert, altes Passwort ungültig.
        var reset = await McpMcp.Server.AdminCommands.ResetUiAdminAsync(users, username, CancellationToken.None);
        reset.WasExisting.Should().BeTrue();
        reset.Password.Should().NotBe(created.Password);
        (await users.ValidateCredentialsAsync(username, reset.Password, CancellationToken.None)).Should().NotBeNull();
        (await users.ValidateCredentialsAsync(username, created.Password, CancellationToken.None))
            .Should().BeNull("das alte Passwort darf nach dem Reset nicht mehr gelten");

        // Notfall-API-Key: neue Identität mit Global-Grant, Key ist sofort gültig.
        var directory = new InMemoryRbacDirectory();
        var rbac = new PersistentRbacStore(Factory, directory);
        var keys = new ApiKeyService(Factory);
        var recovery = await McpMcp.Server.AdminCommands.IssueBootstrapKeyAsync(rbac, keys, CancellationToken.None);

        recovery.ApiKey.Should().StartWith("mcpk_");
        var identityId = await keys.ValidateAsync(recovery.ApiKey, CancellationToken.None);
        identityId.Should().NotBeNull("der ausgegebene Key muss sofort validieren");

        var authorization = new AuthorizationService(directory);
        authorization.Evaluate(identityId!.Value, new PermissionScope(null, null), ToolAction.UseTool)
            .Allowed.Should().BeTrue("die Notfall-Identität trägt einen Global-Grant");
    }

    [SkippableFact]
    public async Task Fresh_database_is_created_from_migrations()
    {
        MarkSkippedIfUnavailable();
        IDbContextFactory<McpMcpDbContext> factory = new TestDbContextFactory(await CreateFreshOptionsAsync("fresh"));

        var outcome = await new DatabaseInitializer(factory).InitializeAsync(CancellationToken.None);

        outcome.Should().Be(DatabaseInitOutcome.CreatedFromMigrations);
        await using var db = await factory.CreateDbContextAsync();
        (await db.Database.GetAppliedMigrationsAsync()).Should().ContainSingle("die InitialCreate-Migration");
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await db.Identities.CountAsync()).Should().Be(0, "das Schema ist nutzbar");
    }

    /// <summary>
    /// Der eigentliche v1.1-Upgrade-Nachweis: eine v1.0-Datenbank (per EnsureCreated erzeugt, ohne
    /// Migrationshistorie) darf beim Start weder scheitern noch Daten verlieren — sie wird gestempelt.
    /// </summary>
    [SkippableFact]
    public async Task Legacy_v1_database_is_baselined_without_data_loss()
    {
        MarkSkippedIfUnavailable();
        IDbContextFactory<McpMcpDbContext> factory = new TestDbContextFactory(await CreateFreshOptionsAsync("legacy"));
        var identityId = Guid.NewGuid();

        // v1.0-Zustand simulieren: Schema ohne Migrationshistorie + Bestandsdaten.
        await using (var legacy = await factory.CreateDbContextAsync())
        {
            await legacy.Database.EnsureCreatedAsync();
            legacy.Identities.Add(new IdentityRow
            {
                Id = identityId, Name = "bestandsagent", Kind = 0, RolesJson = "[]",
            });
            await legacy.SaveChangesAsync();
            (await legacy.Database.GetAppliedMigrationsAsync()).Should().BeEmpty("v1.0 kannte keine Migrationen");
        }

        var outcome = await new DatabaseInitializer(factory).InitializeAsync(CancellationToken.None);

        outcome.Should().Be(DatabaseInitOutcome.BaselinedLegacySchema);
        await using (var upgraded = await factory.CreateDbContextAsync())
        {
            (await upgraded.Identities.SingleAsync()).Name
                .Should().Be("bestandsagent", "das Upgrade darf keine Bestandsdaten anfassen");
            (await upgraded.Database.GetAppliedMigrationsAsync())
                .Should().ContainSingle("die Baseline ist als angewendet gestempelt");
            (await upgraded.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        }

        // Zweiter Start derselben Instanz: normal migriert, nicht erneut gestempelt.
        (await new DatabaseInitializer(factory).InitializeAsync(CancellationToken.None))
            .Should().Be(DatabaseInitOutcome.Migrated, "Initialisierung ist idempotent");
    }

    private async Task WaitForRowCountAsync(int expected, int timeoutMs = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await using var db = await Factory.CreateDbContextAsync();
            if (await db.AuditEvents.CountAsync() >= expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Audit-Zeilen erreichten nicht {expected} binnen {timeoutMs} ms.");
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
        => haystack.AsSpan().IndexOf(needle) >= 0;
}
