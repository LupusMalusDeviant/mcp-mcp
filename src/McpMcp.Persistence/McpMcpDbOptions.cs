using Microsoft.EntityFrameworkCore;

namespace McpMcp.Persistence;

/// <summary>
/// Zentrale Provider-Verdrahtung inklusive Migrations-Assembly (v1.1). SQLite und PostgreSQL
/// erzeugen unterschiedliches DDL, daher hat jeder Provider eine eigene Migrations-Assembly
/// (EF-Core-Standardvorgehen). Host und Tests konfigurieren den Context ausschließlich hierüber,
/// damit Laufzeit und Tests denselben Migrationspfad nutzen.
/// </summary>
public static class McpMcpDbOptions
{
    public const string Sqlite = "sqlite";
    public const string Postgres = "postgres";

    public const string SqliteMigrationsAssembly = "McpMcp.Persistence.Migrations.Sqlite";
    public const string PostgresMigrationsAssembly = "McpMcp.Persistence.Migrations.Postgres";

    public static bool IsPostgres(string? provider)
        => string.Equals(provider, Postgres, StringComparison.OrdinalIgnoreCase);

    public static DbContextOptionsBuilder UseMcpMcpDatabase(
        this DbContextOptionsBuilder builder, string? provider, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (IsPostgres(provider))
        {
            builder.UseNpgsql(connectionString, o => o.MigrationsAssembly(PostgresMigrationsAssembly));
        }
        else
        {
            builder.UseSqlite(connectionString, o => o.MigrationsAssembly(SqliteMigrationsAssembly));
        }

        return builder;
    }

    /// <summary>Generische Überladung, damit <c>DbContextOptionsBuilder&lt;McpMcpDbContext&gt;</c> seinen Typ behält.</summary>
    public static DbContextOptionsBuilder<TContext> UseMcpMcpDatabase<TContext>(
        this DbContextOptionsBuilder<TContext> builder, string? provider, string connectionString)
        where TContext : DbContext
    {
        UseMcpMcpDatabase((DbContextOptionsBuilder)builder, provider, connectionString);
        return builder;
    }
}
