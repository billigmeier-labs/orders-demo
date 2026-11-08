namespace Orders.Api.Domain;

public enum OrderStatus
{
    Pending,
    Paid,
    Shipped,
    Delivered,
    Cancelled,
}

internal static class OrderStatusRules
{
    /// <summary>Pending → Paid → Shipped → Delivered; Pending or Paid → Cancelled.</summary>
    public static bool CanChange(OrderStatus from, OrderStatus to) => (from, to) switch
    {
        (OrderStatus.Pending, OrderStatus.Paid) => true,
        (OrderStatus.Paid, OrderStatus.Shipped) => true,
        (OrderStatus.Shipped, OrderStatus.Delivered) => true,
        (OrderStatus.Pending or OrderStatus.Paid, OrderStatus.Cancelled) => true,
        _ => false,
    };
}
