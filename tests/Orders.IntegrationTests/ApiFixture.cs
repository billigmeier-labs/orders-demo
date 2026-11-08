using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Orders.IntegrationTests.ApiFixture))]

namespace Orders.IntegrationTests;

/// <summary>
/// One PostgreSQL container for the whole test run, and one API on its own database.
/// Tests create their own customers and products, so they can share it and run in parallel.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public OrdersApiFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        Factory = CreateFactory("orders_tests");
    }

    /// <summary>An API on a fresh database of its own.</summary>
    public OrdersApiFactory CreateFactory(string database, bool seed = false) =>
        new(ConnectionString(database), seed);

    public string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database }.ConnectionString;

    public async ValueTask DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }
}

public sealed class OrdersApiFactory(string connectionString, bool seed = false) : WebApplicationFactory<Program>
{
    public const string Version = "1.2.3";
    public const string Commit = "abc1234";
    public const string Environment = "test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Orders", connectionString);
        builder.UseSetting("MIGRATE_ON_STARTUP", "true");
        builder.UseSetting("SEED_DATA", seed ? "true" : "false");
        builder.UseSetting("APP_VERSION", Version);
        builder.UseSetting("APP_COMMIT", Commit);
        builder.UseSetting("APP_ENVIRONMENT", Environment);
    }
}
