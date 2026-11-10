using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Orders.Api.Data;

namespace Orders.UnitTests;

public class DatabaseStartupTests
{
    [Fact]
    public async Task PrepareDatabase_DoesNothingWhenNeitherMigratingNorSeeding()
    {
        await using var app = BuildApp(migrate: false, seed: false);

        // Nothing resolves the database context, so the throwing registration is never hit.
        await app.PrepareDatabaseAsync();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PrepareDatabase_UsesTheDatabaseWhenMigratingOrSeeding(bool migrate, bool seed)
    {
        await using var app = BuildApp(migrate, seed);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => app.PrepareDatabaseAsync());
        Assert.Equal("database used", ex.Message);
    }

    private static WebApplication BuildApp(bool migrate, bool seed)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["MIGRATE_ON_STARTUP"] = migrate ? "true" : "false";
        builder.Configuration["SEED_DATA"] = seed ? "true" : "false";
        builder.Services.AddScoped<OrdersDbContext>(_ => throw new InvalidOperationException("database used"));
        return builder.Build();
    }
}
