using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orders.Api.Domain;

namespace Orders.Api.Data;

internal sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();

    /// <summary>
    /// ConnectionStrings__Orders. It may leave out the password: Npgsql then reads PGPASSWORD.
    /// GSS (Kerberos) is off unless the string asks for it; the runtime image has no libgssapi.
    /// </summary>
    public static string ConnectionString(IConfiguration configuration)
    {
        var builder = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Orders"));
        if (builder.GssEncryptionMode == GssEncryptionMode.Prefer) // Npgsql's default
        {
            builder.GssEncryptionMode = GssEncryptionMode.Disable;
        }

        return builder.ConnectionString;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(customer =>
        {
            customer.Property(c => c.Name).HasMaxLength(200);
            customer.Property(c => c.Email).HasMaxLength(320);
            customer.HasIndex(c => c.Email).IsUnique();
        });

        modelBuilder.Entity<Product>(product =>
        {
            product.Property(p => p.Sku).HasMaxLength(50);
            product.Property(p => p.Name).HasMaxLength(200);
            product.Property(p => p.Price).HasPrecision(12, 2);
            product.Property(p => p.Version).IsRowVersion();
            product.HasIndex(p => p.Sku).IsUnique();
        });

        modelBuilder.Entity<Order>(order =>
        {
            order.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            order.Property(o => o.Version).IsRowVersion();
            order.HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);
            order.HasMany(o => o.Lines).WithOne().HasForeignKey("OrderId").OnDelete(DeleteBehavior.Cascade);
            order.HasIndex(o => o.CreatedAt);
            order.HasIndex(o => o.Status);
            order.Ignore(o => o.Total);
        });

        modelBuilder.Entity<OrderLine>(line =>
        {
            line.ToTable("OrderLines");
            line.Property(l => l.UnitPrice).HasPrecision(12, 2);
            line.HasIndex("OrderId", nameof(OrderLine.Number)).IsUnique();
            line.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
            line.Ignore(l => l.LineTotal);
        });
    }
}
