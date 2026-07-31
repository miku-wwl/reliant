using Microsoft.EntityFrameworkCore;
using Reliant.Infrastructure.Persistence;

namespace Reliant.Migrator;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=reliant;Username=reliant;Password=reliant-dev";

        var options = new DbContextOptionsBuilder<ReliantDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        using var db = new ReliantDbContext(options);

        Console.WriteLine("Applying migrations...");
        await db.Database.MigrateAsync();
        Console.WriteLine("Migrations applied successfully.");
    }
}
