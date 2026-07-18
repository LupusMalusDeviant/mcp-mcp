using McpMcp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace McpMcp.Persistence.Migrations.Sqlite;

/// <summary>Ermöglicht <c>dotnet ef migrations add</c> ohne laufenden Host. Der Connection-String ist nur ein Platzhalter fürs Scaffolding.</summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<McpMcpDbContext>
{
    public McpMcpDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<McpMcpDbContext>()
            .UseMcpMcpDatabase(McpMcpDbOptions.Sqlite, "Data Source=designtime.db")
            .Options);
}
