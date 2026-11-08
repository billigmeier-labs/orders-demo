using Orders.Api.Domain;

namespace Orders.UnitTests;

internal static class TestData
{
    public static Customer Customer() =>
        new(Guid.NewGuid(), "Ada Lovelace", "ada@example.com", DateTimeOffset.UnixEpoch);

    public static Product Product(decimal price = 10m, int stock = 10) =>
        new(Guid.NewGuid(), "SKU-1", "Widget", price, stock);

    public static Order Order(OrderStatus status = OrderStatus.Pending)
    {
        var order = new Order(Guid.NewGuid(), Customer(), DateTimeOffset.UnixEpoch);
        OrderStatus[] path = status switch
        {
            OrderStatus.Paid => [OrderStatus.Paid],
            OrderStatus.Shipped => [OrderStatus.Paid, OrderStatus.Shipped],
            OrderStatus.Delivered => [OrderStatus.Paid, OrderStatus.Shipped, OrderStatus.Delivered],
            OrderStatus.Cancelled => [OrderStatus.Cancelled],
            _ => [],
        };
        foreach (var next in path)
        {
            Assert.True(order.TryChangeStatus(next));
        }

        return order;
    }
}
