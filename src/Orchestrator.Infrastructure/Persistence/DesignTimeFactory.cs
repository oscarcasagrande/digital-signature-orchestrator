using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orchestrator.Infrastructure.Persistence;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<OrchestratorDbContext>
{
    public OrchestratorDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseNpgsql("Host=localhost;Database=design;Username=postgres;Password=postgres")
        .UseSnakeCaseNamingConvention().Options);
}
