using Orders.Api.Domain;

namespace Orders.UnitTests;

public class OrderTotalTests
{
    [Fact]
    public void Total_IsSumOfLineTotals()
    {
        var order = TestData.Order();

        order.TryAddLine(TestData.Product(price: 19.99m), 3);
        order.TryAddLine(TestData.Product(price: 0.01m), 1);

        Assert.Equal(59.97m, order.Lines[0].LineTotal);
        Assert.Equal(59.98m, order.Total);
    }

    [Fact]
    public void Total_EmptyOrder_IsZero() =>
        Assert.Equal(0m, TestData.Order().Total);

    [Fact]
    public void TryAddLine_CopiesUnitPriceFromProduct()
    {
        var product = TestData.Product(price: 12.50m);
        var order = TestData.Order();

        order.TryAddLine(product, 2);

        var line = Assert.Single(order.Lines);
        Assert.Equal(product.Id, line.ProductId);
        Assert.Equal(12.50m, line.UnitPrice);
        Assert.Equal(2, line.Quantity);
    }

    [Fact]
    public void TryAddLine_OrderNotPending_Throws()
    {
        var order = TestData.Order(OrderStatus.Paid);

        Assert.Throws<InvalidOperationException>(() => order.TryAddLine(TestData.Product(), 1));
    }
}
