using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Orders.Api.Data;
using Orders.Api.Domain;
using Orders.Api.Infrastructure;

namespace Orders.Api.Features.Orders;

internal static class OrderEndpoints
{
    public static void MapOrders(this IEndpointRouteBuilder api)
    {
        var orders = api.MapGroup("/orders").WithTags("Orders");
        orders.MapGet("/", List);
        orders.MapGet("/{id:guid}", Get);
        orders.MapPost("/", Create);
        orders.MapPost("/{id:guid}/status", ChangeStatus);
    }

    private static async Task<Ok<PagedResponse<OrderSummaryResponse>>> List(
        OrdersDbContext db,
        OrderStatus? status,
        Guid? customerId,
        CancellationToken ct,
        [Range(1, 100_000)] int page = 1,
        [Range(1, 100)] int pageSize = 20) =>
        TypedResults.Ok(await QueryAsync(db, status, customerId, page, pageSize, ct));

    /// <summary>Shared by <c>GET /api/orders</c> and <c>GET /api/customers/{id}/orders</c>: newest first, paged.</summary>
    internal static async Task<PagedResponse<OrderSummaryResponse>> QueryAsync(
        OrdersDbContext db, OrderStatus? status, Guid? customerId, int page, int pageSize, CancellationToken ct)
    {
        var query = db.Orders.AsQueryable();
        if (status is not null)
        {
            query = query.Where(o => o.Status == status);
        }

        if (customerId is not null)
        {
            query = query.Where(o => o.CustomerId == customerId);
        }

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(o => new OrderSummaryResponse(
                o.Id, o.CustomerId, o.Customer.Name, o.Status, Math.Round(o.Lines.Sum(l => l.UnitPrice * l.Quantity), 2), o.Lines.Count, o.CreatedAt))
            .ToListAsync(ct);

        return new PagedResponse<OrderSummaryResponse>(items, page, pageSize, totalCount);
    }

    private static async Task<Results<Ok<OrderResponse>, ProblemHttpResult>> Get(Guid id, OrdersDbContext db, CancellationToken ct) =>
        await LoadAsync(db, id, ct) is { } order
            ? TypedResults.Ok(OrderResponse.From(order))
            : Problems.NotFound("Order", id);

    private static async Task<Results<Created<OrderResponse>, ValidationProblem, ProblemHttpResult>> Create(
        CreateOrderRequest request, OrdersDbContext db, TimeProvider clock, FaultInjector faults, CancellationToken ct)
    {
        await faults.MaybeFailOrderAsync();

        var customer = await db.Customers.FindAsync([request.CustomerId], ct);
        if (customer is null)
        {
            return Problems.Invalid("customerId", $"Customer {request.CustomerId} does not exist.");
        }

        // The same product on two lines becomes one line with the summed quantity.
        var wanted = request.Lines
            .GroupBy(l => l.ProductId)
            .Select(g => (ProductId: g.Key, Quantity: g.Sum(l => l.Quantity)))
            .ToList();
        var productIds = wanted.Select(w => w.ProductId).ToList();
        var products = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        var unknown = productIds.Where(id => !products.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
        {
            return Problems.Invalid("lines", $"Unknown product(s): {string.Join(", ", unknown)}.");
        }

        var order = new Order(Guid.CreateVersion7(), customer, clock.GetUtcNow());
        var outOfStock = new List<string>();
        foreach (var (productId, quantity) in wanted)
        {
            if (!order.TryAddLine(products[productId], quantity))
            {
                outOfStock.Add(products[productId].Sku);
            }
        }

        if (outOfStock.Count > 0)
        {
            return Problems.Conflict($"Not enough stock for {string.Join(", ", outOfStock)}.");
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/orders/{order.Id}", OrderResponse.From(order));
    }

    private static async Task<Results<Ok<OrderResponse>, ProblemHttpResult>> ChangeStatus(
        Guid id, ChangeStatusRequest request, OrdersDbContext db, CancellationToken ct)
    {
        var order = await LoadAsync(db, id, ct);
        if (order is null)
        {
            return Problems.NotFound("Order", id);
        }

        var next = request.Status!.Value;
        if (!order.TryChangeStatus(next))
        {
            return Problems.Conflict($"An order can't go from {order.Status} to {next}.");
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(OrderResponse.From(order));
    }

    private static Task<Order?> LoadAsync(OrdersDbContext db, Guid id, CancellationToken ct) =>
        db.Orders
            .Include(o => o.Customer)
            .Include(o => o.Lines.OrderBy(l => l.Number)).ThenInclude(l => l.Product)
            .AsSplitQuery()
            .SingleOrDefaultAsync(o => o.Id == id, ct);
}
