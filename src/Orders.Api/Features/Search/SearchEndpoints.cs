using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.FeatureManagement;
using Npgsql;
using Orders.Api.Data;
using Orders.Api.Features.Orders;
using Orders.Api.Features.Products;
using Orders.Api.Infrastructure;

namespace Orders.Api.Features.Search;

public sealed record SearchResponse(IReadOnlyList<ProductResponse> Products, IReadOnlyList<OrderSummaryResponse> Orders);

internal static class SearchEndpoints
{
    internal const int MaxResults = 20;

    // Npgsql emits ESCAPE '' (no escape character) unless one is given, so EscapeLike would have no effect.
    private const string LikeEscape = @"\";

    public static void MapSearch(this IEndpointRouteBuilder api) =>
        api.MapGet("/search", Search).WithTags("Search");

    /// <summary>
    /// Products by name or SKU; orders by id prefix or customer name. Case-insensitive.
    /// With the searchV2 flag on, <see cref="SearchV2"/> answers instead (ranked, word-prefix matching).
    /// </summary>
    private static async Task<Results<Ok<SearchResponse>, ValidationProblem>> Search(
        [Required, StringLength(100)] string q, OrdersDbContext db, IFeatureManager features, FaultInjector faults, CancellationToken ct)
    {
        var term = q.Trim().ToLowerInvariant();
        if (term.Length == 0)
        {
            return Problems.Invalid("q", "The q field is required.");
        }

        await faults.MaybeSlowSearchAsync(ct);

        var result = await features.IsEnabledAsync(Flags.SearchV2)
            ? await SearchV2.SearchAsync(db, term, ct)
            : await SearchV1Async(db, term, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<SearchResponse> SearchV1Async(OrdersDbContext db, string term, CancellationToken ct)
    {
        var contains = $"%{EscapeLike(term)}%";

        // The product and order lookups don't depend on each other, so run them side by side. A DbContext runs
        // one query at a time, so the order lookup gets a second context on its own connection.
        var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct);
        await using var ordersDb = new OrdersDbContext(new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(connection).Options);

        var products = db.Products
            .Where(p => EF.Functions.ILike(p.Name, contains, LikeEscape) || EF.Functions.ILike(p.Sku, contains, LikeEscape))
            .OrderBy(p => p.Sku)
            .Take(MaxResults)
            .Select(p => new ProductResponse(p.Id, p.Sku, p.Name, p.Price, p.Stock))
            .ToListAsync(ct);

        var orders = ordersDb.Orders
            .Where(o => o.Id.ToString().StartsWith(term) || EF.Functions.ILike(o.Customer.Name, contains, LikeEscape))
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Take(MaxResults)
            .Select(o => new OrderSummaryResponse(
                o.Id, o.CustomerId, o.Customer.Name, o.Status, Math.Round(o.Lines.Sum(l => l.UnitPrice * l.Quantity), 2), o.Lines.Count, o.CreatedAt))
            .ToListAsync(ct);

        await Task.WhenAll(products, orders);
        return new SearchResponse(await products, await orders);
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}
