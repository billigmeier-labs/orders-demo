using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Orders.Api.Data;
using Orders.Api.Domain;
using Orders.Api.Infrastructure;

namespace Orders.Api.Features.Products;

public sealed record ProductResponse(Guid Id, string Sku, string Name, decimal Price, int Stock)
{
    internal static ProductResponse From(Product p) => new(p.Id, p.Sku, p.Name, p.Price, p.Stock);
}

public sealed record CreateProductRequest(
    [Required, StringLength(50)] string Sku,
    [Required, StringLength(200)] string Name,
    [Required, Money] decimal? Price,
    [Required, Range(0, 1_000_000)] int? Stock);

public sealed record RestockRequest([property: Required, Range(1, 10_000)] int? Quantity);

internal static class ProductEndpoints
{
    public static void MapProducts(this IEndpointRouteBuilder api)
    {
        var products = api.MapGroup("/products").WithTags("Products");
        products.MapGet("/", List);
        products.MapGet("/{id:guid}", Get);
        products.MapPost("/", Create);
        products.MapPost("/{id:guid}/restock", Restock);
    }

    private static async Task<Ok<List<ProductResponse>>> List(OrdersDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Products
            .OrderBy(p => p.Sku)
            .Select(p => new ProductResponse(p.Id, p.Sku, p.Name, p.Price, p.Stock))
            .ToListAsync(ct));

    private static async Task<Results<Ok<ProductResponse>, ProblemHttpResult>> Get(Guid id, OrdersDbContext db, CancellationToken ct) =>
        await db.Products.FindAsync([id], ct) is { } product
            ? TypedResults.Ok(ProductResponse.From(product))
            : Problems.NotFound("Product", id);

    private static async Task<Results<Created<ProductResponse>, ProblemHttpResult>> Create(
        CreateProductRequest request, OrdersDbContext db, CancellationToken ct)
    {
        var sku = request.Sku.Trim().ToUpperInvariant();
        if (await db.Products.AnyAsync(p => p.Sku == sku, ct))
        {
            return Problems.Conflict($"A product with SKU {sku} already exists.");
        }

        var product = new Product(Guid.CreateVersion7(), sku, request.Name.Trim(), request.Price!.Value, request.Stock!.Value);
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/products/{product.Id}", ProductResponse.From(product));
    }

    /// <summary>Adds stock with one atomic UPDATE, so parallel restocks (and order changes) never overwrite each other.</summary>
    private static async Task<Results<Ok<ProductResponse>, ProblemHttpResult>> Restock(
        Guid id, RestockRequest request, OrdersDbContext db, CancellationToken ct)
    {
        var quantity = request.Quantity!.Value;
        var updated = await db.Products
            .Where(p => p.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock + quantity), ct);
        if (updated == 0)
        {
            return Problems.NotFound("Product", id);
        }

        var product = await db.Products.AsNoTracking().Where(p => p.Id == id)
            .Select(p => new ProductResponse(p.Id, p.Sku, p.Name, p.Price, p.Stock))
            .SingleOrDefaultAsync(ct);
        return product is null ? Problems.NotFound("Product", id) : TypedResults.Ok(product);
    }
}
