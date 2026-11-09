using System.Globalization;
using Orders.Api.Domain;

namespace Orders.Api.Features.Products;

/// <summary>Threshold parsing and filter/sort for GET /api/products/low-stock.</summary>
internal static class LowStock
{
    public const int DefaultThreshold = 5;

    /// <summary>Null means the default; otherwise a non-negative integer. Returns false for negative or non-numeric input.</summary>
    public static bool TryParseThreshold(string? raw, out int threshold)
    {
        threshold = DefaultThreshold;
        if (raw is null)
        {
            return true;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out threshold) && threshold >= 0;
    }

    /// <summary>Products with stock strictly below the threshold, lowest stock first, ties by SKU.</summary>
    public static IQueryable<Product> Query(IQueryable<Product> products, int threshold) =>
        products
            .Where(p => p.Stock < threshold)
            .OrderBy(p => p.Stock)
            .ThenBy(p => p.Sku);
}
