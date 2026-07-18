using McpMcp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace McpMcp.Persistence.Migrations.Postgres;

/// <summary>Ermöglicht <c>dotnet ef migrations add</c> ohne laufenden Host. Der Connection-String ist nur ein Platzhalter fürs Scaffolding.</summary>
public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<McpMcpDbContext>
{
    public McpMcpDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<McpMcpDbContext>()
            .UseMcpMcpDatabase(McpMcpDbOptions.Postgres, "Host=localhost;Database=mcpmcp;Username=designtime;Password=designtime")
            .Options);
}
