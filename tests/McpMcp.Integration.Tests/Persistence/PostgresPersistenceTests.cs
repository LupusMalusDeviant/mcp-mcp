using McpMcp.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace McpMcp.Integration.Tests.Persistence;

/// <summary>
/// Identische Suite gegen echtes PostgreSQL (ADR-0007: Zwei-Provider-Disziplin per CI).
/// Ohne erreichbaren Docker-Daemon (z. B. Windows-CI-Runner mit Windows-Containern)
/// werden die Tests übersprungen — der Ubuntu-CI-Lauf trägt den Postgres-Nachweis.
/// </summary>
public sealed class PostgresPersistenceTests : PersistenceTestsBase
{
    private PostgreSqlContainer? _container;
    private string? _unavailableReason;

    protected override async Task<DbContextOptions<McpMcpDbContext>?> CreateOptionsAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
            return new DbContextOptionsBuilder<McpMcpDbContext>()
                .UseNpgsql(_container.GetConnectionString())
                .Options;
        }
        catch (Exception ex)
        {
            _unavailableReason = $"PostgreSQL-Testcontainer nicht startbar: {ex.Message}";
            return null;
        }
    }

    protected override void MarkSkippedIfUnavailable()
        => Skip.If(_unavailableReason is not null, _unavailableReason);

    public override async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
