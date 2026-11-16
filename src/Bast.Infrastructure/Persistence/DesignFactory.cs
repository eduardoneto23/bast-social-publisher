using Bast.Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bast.Infrastructure.Persistence;

public static class BastSqlite
{
    /// <summary>As migrações ficam neste projeto; o contexto, em Bast.Application.</summary>
    public static DbContextOptionsBuilder UseBastSqlite(this DbContextOptionsBuilder builder, string databasePath) =>
        builder.UseSqlite($"Data Source={databasePath}", sqlite => sqlite.MigrationsAssembly(typeof(BastSqlite).Assembly.GetName().Name));
}

/// <summary>Usado apenas pelas ferramentas de migração (dotnet ef).</summary>
public sealed class BastDbContextDesignFactory : IDesignTimeDbContextFactory<BastDbContext>
{
    public BastDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<BastDbContext>();
        builder.UseBastSqlite(Path.Combine(Path.GetTempPath(), "bast-design-time.db"));
        return new BastDbContext(builder.Options);
    }
}
