using System.Text;
using FluentAssertions;
using McpMcp.Abstractions;
using McpMcp.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace McpMcp.Integration.Tests.Persistence;

public sealed class SqlitePersistenceTests : PersistenceTestsBase
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"mcpmcp-test-{Guid.NewGuid():N}.db");

    protected override Task<DbContextOptions<McpMcpDbContext>?> CreateOptionsAsync()
        => Task.FromResult<DbContextOptions<McpMcpDbContext>?>(
            new DbContextOptionsBuilder<McpMcpDbContext>()
                .UseSqlite($"Data Source={_dbPath}")
                .Options);

    protected override void MarkSkippedIfUnavailable()
    {
        // SQLite ist immer verfügbar.
    }

    public override Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        return Task.CompletedTask;
    }

    /// <summary>WP3-DoD: die SQLite-Datei selbst enthält keine Klartext-Secrets (Roh-Byte-Scan).</summary>
    [Fact]
    public async Task Database_file_contains_no_plaintext_secret()
    {
        var store = new EfUpstreamConfigStore(Factory, DataProtection);
        await store.AppendVersionAsync(ServerId.New(), ConfigWithSecret(), CancellationToken.None);

        SqliteConnection.ClearAllPools();
        var fileBytes = await File.ReadAllBytesAsync(_dbPath);

        fileBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("SUPERSECRET_VALUE_12345"))
            .Should().Be(-1, "die DB-Datei darf das Secret nie im Klartext enthalten (NFR-04, WP3-DoD)");
        fileBytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes("ConfigVersions"))
            .Should().BeGreaterThan(0, "Gegenprobe: die Datei ist lesbar und enthält das Schema");
    }
}
