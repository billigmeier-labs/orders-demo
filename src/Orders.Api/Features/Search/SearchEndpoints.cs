using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Orders.Api.Data;
using Orders.Api.Features.Orders;
using Orders.Api.Features.Products;
using Orders.Api.Infrastructure;

namespace Orders.Api.Features.Search;

public sealed record SearchResponse(IReadOnlyList<ProductResponse> Products, IReadOnlyList<OrderSummaryResponse> Orders);

internal static class SearchEndpoints
{
    private const int MaxResults = 20;

    public static void MapSearch(this IEndpointRouteBuilder api) =>
        api.MapGet("/search", Search).WithTags("Search");

    /// <summary>Products by name or SKU; orders by id prefix or customer name. Case-insensitive.</summary>
    private static async Task<Results<Ok<SearchResponse>, ValidationProblem>> Search([Required, StringLength(100)] string q, OrdersDbContext db, CancellationToken ct)
    {
        var term = q.Trim().ToLowerInvariant();
        if (term.Length == 0)
        {
            return Problems.Invalid("q", "The q field is required.");
        }

        var contains = $"%{EscapeLike(term)}%";

        var products = await db.Products
            .Where(p => EF.Functions.ILike(p.Name, contains) || EF.Functions.ILike(p.Sku, contains))
            .OrderBy(p => p.Sku)
            .Take(MaxResults)
            .Select(p => new ProductResponse(p.Id, p.Sku, p.Name, p.Price, p.Stock))
            .ToListAsync(ct);

        var orders = await db.Orders
            .Where(o => o.Id.ToString().StartsWith(term) || EF.Functions.ILike(o.Customer.Name, contains))
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Take(MaxResults)
            .Select(o => new OrderSummaryResponse(
                o.Id, o.CustomerId, o.Customer.Name, o.Status, Math.Round(o.Lines.Sum(l => l.UnitPrice * l.Quantity), 2), o.Lines.Count, o.CreatedAt))
            .ToListAsync(ct);

        return TypedResults.Ok(new SearchResponse(products, orders));
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}
