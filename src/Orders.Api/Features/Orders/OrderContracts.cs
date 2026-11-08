using System.ComponentModel.DataAnnotations;
using Orders.Api.Domain;

namespace Orders.Api.Features.Orders;

public sealed record CreateOrderRequest(
    Guid CustomerId,
    [Required, MinLength(1), MaxLength(50)] List<CreateOrderLineRequest> Lines);

public sealed record CreateOrderLineRequest(Guid ProductId, [Range(1, 1000)] int Quantity);

public sealed record ChangeStatusRequest([Required] OrderStatus? Status);

public sealed record OrderSummaryResponse(
    Guid Id, Guid CustomerId, string CustomerName, OrderStatus Status, decimal Total, int LineCount, DateTimeOffset CreatedAt);

public sealed record OrderLineResponse(Guid ProductId, string Sku, string Name, int Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record OrderResponse(
    Guid Id,
    Guid CustomerId,
    string CustomerName,
    OrderStatus Status,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OrderLineResponse> Lines,
    decimal Total)
{
    /// <summary>Maps an order loaded with its customer and line products.</summary>
    internal static OrderResponse From(Order order) => new(
        order.Id,
        order.CustomerId,
        order.Customer.Name,
        order.Status,
        order.CreatedAt,
        [.. order.Lines.Select(l => new OrderLineResponse(l.ProductId, l.Product.Sku, l.Product.Name, l.Quantity, l.UnitPrice, l.LineTotal))],
        order.Total);
}

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
