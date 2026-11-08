using Microsoft.Extensions.Configuration;
using Npgsql;
using Orders.Api.Data;

namespace Orders.UnitTests;

public class ConnectionStringTests
{
    private static string Resolve(string connectionString) =>
        OrdersDbContext.ConnectionString(new ConfigurationBuilder()
            .AddInMemoryCollection([KeyValuePair.Create<string, string?>("ConnectionStrings:Orders", connectionString)])
            .Build());

    [Fact]
    public void WithoutPassword_StaysWithoutPassword()
    {
        // In AWS the password comes from PGPASSWORD, which Npgsql reads when the string has none.
        var resolved = new NpgsqlConnectionStringBuilder(Resolve("Host=db;Database=orders;Username=orders"));

        Assert.Null(resolved.Password);
        Assert.Equal(("db", "orders", "orders"), (resolved.Host, resolved.Database, resolved.Username));
    }

    [Fact]
    public void GssEncryption_DisabledByDefault() =>
        Assert.Equal(GssEncryptionMode.Disable, new NpgsqlConnectionStringBuilder(Resolve("Host=db")).GssEncryptionMode);

    [Fact]
    public void GssEncryption_KeptWhenSet() =>
        Assert.Equal(GssEncryptionMode.Require, new NpgsqlConnectionStringBuilder(Resolve("Host=db;GSS Encryption Mode=Require")).GssEncryptionMode);
}
