using Microsoft.EntityFrameworkCore;

namespace Orders.Api.Data;

internal static class DatabaseStartup
{
    /// <summary>
    /// Applies migrations when MIGRATE_ON_STARTUP=true and seeds an empty database when SEED_DATA=true.
    /// Deployed environments run a migration bundle instead of migrating on startup.
    /// </summary>
    public static async Task PrepareDatabaseAsync(this WebApplication app)
    {
        var migrate = app.Configuration.GetValue<bool>("MIGRATE_ON_STARTUP");
        var seed = app.Configuration.GetValue<bool>("SEED_DATA");
        if (!migrate && !seed)
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

        if (migrate)
        {
            await db.Database.MigrateAsync();
        }

        if (seed)
        {
            await SeedData.SeedIfEmptyAsync(db);
        }
    }
}
