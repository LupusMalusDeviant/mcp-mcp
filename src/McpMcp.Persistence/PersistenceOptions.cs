namespace McpMcp.Persistence;

/// <summary>Betriebsparameter der Persistenz (ADR-0007).</summary>
public sealed record PersistenceOptions
{
    /// <summary>Maximale Wartezeit, bis ein angefangener Audit-Batch geschrieben wird (Crash-Verlustfenster).</summary>
    public TimeSpan AuditFlushInterval { get; init; } = TimeSpan.FromSeconds(1);

    public int AuditMaxBatchSize { get; init; } = 500;

    /// <summary>Aufbewahrung der Audit-Ereignisse (FR-25). Retention ist bei SQLite Betriebspflicht (ADR-0007).</summary>
    public TimeSpan AuditRetention { get; init; } = TimeSpan.FromDays(30);

    public int AuditChannelCapacity { get; init; } = 100_000;
}
