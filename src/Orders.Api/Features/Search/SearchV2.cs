using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Orders.Api.Data;
using Orders.Api.Features.Orders;
using Orders.Api.Features.Products;

namespace Orders.Api.Features.Search;

/// <summary>
/// Search v2 (flag searchV2): PostgreSQL full-text search with word-prefix matching, so "lam" finds
/// "Desk Lamp" and "hop" finds Grace Hopper's orders, ranked by how well they match.
/// Orders whose id starts with the term come first.
/// </summary>
internal static partial class SearchV2
{
    // 'simple' lowercases and splits words without stemming, which suits product names, SKUs and people's names.
    private const string Config = "simple";

    public static async Task<SearchResponse> SearchAsync(OrdersDbContext db, string term, CancellationToken ct)
    {
        var query = PrefixQuery(term);
        if (query.Length == 0)
        {
            return new SearchResponse([], []);
        }

        var products = await db.Products
            .Where(p => EF.Functions.ToTsVector(Config, p.Name + " " + p.Sku).Matches(EF.Functions.ToTsQuery(Config, query)))
            .OrderByDescending(p => EF.Functions.ToTsVector(Config, p.Name + " " + p.Sku).Rank(EF.Functions.ToTsQuery(Config, query)))
            .ThenBy(p => p.Sku)
            .Take(SearchEndpoints.MaxResults)
            .Select(p => new ProductResponse(p.Id, p.Sku, p.Name, p.Price, p.Stock))
            .ToListAsync(ct);

        var orders = await db.Orders
            .Where(o => o.Id.ToString().StartsWith(term)
                || EF.Functions.ToTsVector(Config, o.Customer.Name).Matches(EF.Functions.ToTsQuery(Config, query)))
            .OrderByDescending(o => o.Id.ToString().StartsWith(term))
            .ThenByDescending(o => EF.Functions.ToTsVector(Config, o.Customer.Name).Rank(EF.Functions.ToTsQuery(Config, query)))
            .ThenByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Take(SearchEndpoints.MaxResults)
            .Select(o => new OrderSummaryResponse(
                o.Id, o.CustomerId, o.Customer.Name, o.Status, Math.Round(o.Lines.Sum(l => l.UnitPrice * l.Quantity), 2), o.Lines.Count, o.CreatedAt))
            .ToListAsync(ct);

        return new SearchResponse(products, orders);
    }

    /// <summary>
    /// The search term as a tsquery prefix match: "lam" becomes "lam:*" and "water bottle" becomes
    /// "water:* &amp; bottle:*". Characters that are tsquery syntax (operators, parentheses, quotes) are dropped
    /// so user input can't change the query's meaning. Returns an empty string when no words are left.
    /// </summary>
    internal static string PrefixQuery(string term) => string.Join(
        " & ",
        TsQuerySyntax().Replace(term, string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => $"{word}:*"));

    [GeneratedRegex(@"[&|!():*'<>\\]")]
    private static partial Regex TsQuerySyntax();
}
