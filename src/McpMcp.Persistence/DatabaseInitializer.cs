using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpMcp.Persistence;

/// <summary>Was der Initializer beim Start vorgefunden und getan hat — für Logging und Tests.</summary>
public enum DatabaseInitOutcome
{
    /// <summary>Leere/neue Datenbank: Schema komplett über Migrationen angelegt.</summary>
    CreatedFromMigrations = 0,

    /// <summary>Bestehende v1.0-Datenbank (per EnsureCreated erzeugt, ohne Migrationshistorie): Baseline gestempelt.</summary>
    BaselinedLegacySchema = 1,

    /// <summary>Bereits migrationsverwaltet: ausstehende Migrationen angewendet (ggf. keine).</summary>
    Migrated = 2,
}

/// <summary>
/// Schema-Initialisierung ab v1.1 über EF-Migrationen statt <c>EnsureCreated</c>.
///
/// Der heikle Fall ist das Upgrade: v1.0-Datenbanken wurden per <c>EnsureCreated</c> erzeugt und
/// besitzen daher **keine** <c>__EFMigrationsHistory</c>. Ein blindes <c>Migrate()</c> würde dort
/// CREATE TABLE auf bereits existierende Tabellen fahren und scheitern. Deshalb wird ein solches
/// Alt-Schema erkannt und die Initial-Migration als "bereits angewendet" gestempelt (Baseline),
/// bevor migriert wird — die Daten bleiben unangetastet.
/// </summary>
public sealed partial class DatabaseInitializer
{
    private readonly IDbContextFactory<McpMcpDbContext> _factory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        IDbContextFactory<McpMcpDbContext> factory,
        ILogger<DatabaseInitializer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _logger = logger ?? NullLogger<DatabaseInitializer>.Instance;
    }

    public async Task<DatabaseInitOutcome> InitializeAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var history = db.GetService<IHistoryRepository>();
        var creator = db.GetService<IRelationalDatabaseCreator>();

        var historyExists = await history.ExistsAsync(ct).ConfigureAwait(false);
        var outcome = DatabaseInitOutcome.Migrated;

        if (!historyExists)
        {
            var databaseExists = await creator.ExistsAsync(ct).ConfigureAwait(false);
            var hasTables = databaseExists && await creator.HasTablesAsync(ct).ConfigureAwait(false);

            if (hasTables)
            {
                await StampBaselineAsync(db, history, ct).ConfigureAwait(false);
                outcome = DatabaseInitOutcome.BaselinedLegacySchema;
            }
            else
            {
                outcome = DatabaseInitOutcome.CreatedFromMigrations;
            }
        }

        await db.Database.MigrateAsync(ct).ConfigureAwait(false);

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).Count();
        Log.Initialized(_logger, outcome, applied);
        return outcome;
    }

    /// <summary>
    /// Legt die Migrationshistorie an und trägt die Initial-Migration als angewendet ein, ohne ihr DDL
    /// auszuführen. Die SQL-Skripte stammen aus EF selbst (kein Fremdeingabe-Pfad).
    /// </summary>
    private async Task StampBaselineAsync(McpMcpDbContext db, IHistoryRepository history, CancellationToken ct)
    {
        var baseline = db.Database.GetMigrations().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Keine Migrationen in der Provider-Migrations-Assembly gefunden — ist sie referenziert?");

        var productVersion = typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "10.0.0";

        Log.BaseliningLegacySchema(_logger, baseline);
#pragma warning disable EF1002 // Skripte kommen aus EF, nicht aus Nutzereingaben
        await db.Database.ExecuteSqlRawAsync(history.GetCreateScript(), ct).ConfigureAwait(false);
        await db.Database
            .ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(baseline, productVersion)), ct)
            .ConfigureAwait(false);
#pragma warning restore EF1002
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Datenbank initialisiert ({Outcome}), {AppliedCount} Migration(en) angewendet.")]
        public static partial void Initialized(ILogger logger, DatabaseInitOutcome outcome, int appliedCount);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Bestehendes v1.0-Schema ohne Migrationshistorie erkannt — Migration {Baseline} wird als Baseline gestempelt (kein DDL, Daten bleiben erhalten).")]
        public static partial void BaseliningLegacySchema(ILogger logger, string baseline);
    }
}
