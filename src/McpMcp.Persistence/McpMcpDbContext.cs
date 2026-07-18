using Microsoft.EntityFrameworkCore;

namespace McpMcp.Persistence;

/// <summary>
/// EF-Core-Modell (ADR-0007). Provider-neutral gehalten: JSON-Blobs für Listen,
/// DateTimeOffset immer UTC (Npgsql-timestamptz-Kompatibilität). Die Schema-Erzeugung läuft über
/// EF-Migrationen je Provider; <see cref="DatabaseInitializer"/> stempelt dabei v1.0-Datenbanken,
/// die noch aus der EnsureCreated-Zeit stammen, auf die Baseline.
/// </summary>
public sealed class McpMcpDbContext : DbContext
{
    public McpMcpDbContext(DbContextOptions<McpMcpDbContext> options)
        : base(options)
    {
    }

    public DbSet<ConfigVersionRow> ConfigVersions => Set<ConfigVersionRow>();

    public DbSet<IdentityRow> Identities => Set<IdentityRow>();

    public DbSet<RoleRow> Roles => Set<RoleRow>();

    public DbSet<ProfileRow> Profiles => Set<ProfileRow>();

    public DbSet<ApiKeyRow> ApiKeys => Set<ApiKeyRow>();

    public DbSet<AuditEventRow> AuditEvents => Set<AuditEventRow>();

    public DbSet<UiUserRow> UiUsers => Set<UiUserRow>();

    public DbSet<AssetRow> Assets => Set<AssetRow>();

    public DbSet<ToolDescriptionOverrideRow> ToolDescriptionOverrides => Set<ToolDescriptionOverrideRow>();

    public DbSet<RedactionRuleRow> RedactionRules => Set<RedactionRuleRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConfigVersionRow>(e =>
        {
            e.HasKey(r => new { r.ServerId, r.Version });
            e.Property(r => r.Payload).IsRequired();
        });

        modelBuilder.Entity<IdentityRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).HasMaxLength(200).IsRequired();
            e.Property(r => r.RolesJson).IsRequired();
        });

        modelBuilder.Entity<RoleRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).HasMaxLength(200).IsRequired();
            e.Property(r => r.GrantsJson).IsRequired();
        });

        modelBuilder.Entity<ProfileRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).HasMaxLength(200).IsRequired();
            e.Property(r => r.PinnedToolsJson).IsRequired();
        });

        modelBuilder.Entity<ApiKeyRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Label).HasMaxLength(200).IsRequired();
            e.Property(r => r.Hash).HasMaxLength(500).IsRequired();
            e.HasIndex(r => r.IdentityId);
        });

        modelBuilder.Entity<AuditEventRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedOnAdd();
            e.HasIndex(r => r.Timestamp);
            e.HasIndex(r => r.CallerId);
            e.HasIndex(r => r.ServerId);
            e.HasIndex(r => r.Tool);
            e.HasIndex(r => r.Status);
        });

        modelBuilder.Entity<UiUserRow>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Username).HasMaxLength(200).IsRequired();
            e.HasIndex(r => r.Username).IsUnique();
            e.Property(r => r.PasswordHash).HasMaxLength(500).IsRequired();
        });

        modelBuilder.Entity<AssetRow>(e =>
        {
            e.HasKey(r => new { r.Id, r.Version });
            e.Property(r => r.Name).HasMaxLength(200).IsRequired();
            e.Property(r => r.Content).IsRequired();
        });

        modelBuilder.Entity<ToolDescriptionOverrideRow>(e =>
        {
            e.HasKey(r => r.Tool);
            e.Property(r => r.Tool).HasMaxLength(300);
            e.Property(r => r.Description).IsRequired();
        });

        modelBuilder.Entity<RedactionRuleRow>(e =>
        {
            e.HasKey(r => r.Tool);
            e.Property(r => r.Tool).HasMaxLength(300);
            e.Property(r => r.Patterns).IsRequired();
        });

        // Provider-neutral: Zeitstempel als UTC-Ticks (bigint). SQLite kann DateTimeOffset weder
        // sortieren noch in ExecuteDelete vergleichen; long funktioniert identisch auf beiden Providern.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(UtcTicksConverter.Instance);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(NullableUtcTicksConverter.Instance);
                }
            }
        }
    }

    private static class UtcTicksConverter
    {
        public static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long> Instance =
            new(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
    }

    private static class NullableUtcTicksConverter
    {
        public static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?> Instance =
            new(
                v => v.HasValue ? v.Value.UtcTicks : null,
                v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);
    }
}

/// <summary>Verschlüsselte Config-Version (FR-10). Payload = DataProtection-verschlüsseltes JSON der kompletten UpstreamServerConfig (NFR-04).</summary>
public sealed class ConfigVersionRow
{
    public Guid ServerId { get; set; }

    public int Version { get; set; }

    public byte[] Payload { get; set; } = [];

    public DateTimeOffset SavedAt { get; set; }
}

public sealed class IdentityRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Kind { get; set; }

    public Guid? ProfileId { get; set; }

    public string RolesJson { get; set; } = "[]";
}

public sealed class RoleRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int? RateLimitPerMinute { get; set; }

    public string GrantsJson { get; set; } = "[]";
}

public sealed class ProfileRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool LazyToolsEnabled { get; set; }

    public string PinnedToolsJson { get; set; } = "[]";
}

public sealed class ApiKeyRow
{
    public Guid Id { get; set; }

    public Guid IdentityId { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>Format: {iterations}.{saltBase64}.{hashBase64} — niemals der Klartext-Key (NFR-04).</summary>
    public string Hash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class AuditEventRow
{
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public Guid? CallerId { get; set; }

    public int Origin { get; set; }

    public int Kind { get; set; }

    public Guid? ServerId { get; set; }

    public string? Tool { get; set; }

    public int? Status { get; set; }

    public string? RedactedArgumentsJson { get; set; }

    public long? RequestBytes { get; set; }

    public long? ResponseBytes { get; set; }

    public double? DurationMs { get; set; }

    /// <summary>Profil/Rollen des Aufrufers im Klartext (FR-21).</summary>
    public string? CallerRoles { get; set; }

    /// <summary>Klartext bei Systemereignissen, z.B. Upstream-Zustandswechsel (FR-22).</summary>
    public string? Detail { get; set; }

    /// <summary>Maskierter Ergebnis-Payload — nur im Debug-Modus befüllt (FR-24).</summary>
    public string? RedactedResponseJson { get; set; }
}

public sealed class UiUserRow
{
    public Guid Id { get; set; }

    public string Username { get; set; } = string.Empty;

    /// <summary>PBKDF2-Hash im Format {iterations}.{salt}.{hash} — nie das Klartext-Passwort (NFR-04).</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public int Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Serverseitig überschriebene Tool-Beschreibung (FR-14), Schlüssel ist der namespaced Tool-Name.</summary>
public sealed class ToolDescriptionOverrideRow
{
    public string Tool { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
}

/// <summary>Zusätzliche Redaction-Muster eines Tools (FR-24), kommasepariert.</summary>
public sealed class RedactionRuleRow
{
    public string Tool { get; set; } = string.Empty;

    public string Patterns { get; set; } = string.Empty;
}

/// <summary>Versioniertes Text-Asset (Skill/Prompt/Instruction, FR-40, WP6.4). Append-only pro Version.</summary>
public sealed class AssetRow
{
    public Guid Id { get; set; }

    public int Version { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Content { get; set; } = string.Empty;

    public DateTimeOffset PublishedAt { get; set; }
}
