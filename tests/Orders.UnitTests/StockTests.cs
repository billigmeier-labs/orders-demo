using Orders.Api.Domain;

namespace Orders.UnitTests;

public class StockTests
{
    [Fact]
    public void NewOrder_KeepsDeliveryNotes_AndDefaultsToNull()
    {
        var withNotes = new Order(Guid.NewGuid(), TestData.Customer(), DateTimeOffset.UnixEpoch, "Side door");

        Assert.Equal("Side door", withNotes.DeliveryNotes);
        Assert.Null(TestData.Order().DeliveryNotes);
    }

    [Fact]
    public void TryRemoveStock_Enough_ReducesStock()
    {
        var product = TestData.Product(stock: 5);

        Assert.True(product.TryRemoveStock(5));
        Assert.Equal(0, product.Stock);
    }

    [Fact]
    public void TryRemoveStock_NotEnough_ReturnsFalseAndKeepsStock()
    {
        var product = TestData.Product(stock: 2);

        Assert.False(product.TryRemoveStock(3));
        Assert.Equal(2, product.Stock);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TryRemoveStock_NonPositiveQuantity_Throws(int quantity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TestData.Product().TryRemoveStock(quantity));

    [Fact]
    public void NewProduct_NegativeStock_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TestData.Product(stock: -1));

    [Fact]
    public void TryAddLine_TakesStock()
    {
        var product = TestData.Product(stock: 10);
        var order = TestData.Order();

        Assert.True(order.TryAddLine(product, 4));
        Assert.Equal(6, product.Stock);
    }

    [Fact]
    public void TryAddLine_NotEnoughStock_AddsNoLine()
    {
        var product = TestData.Product(stock: 1);
        var order = TestData.Order();

        Assert.False(order.TryAddLine(product, 2));
        Assert.Empty(order.Lines);
        Assert.Equal(1, product.Stock);
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Paid)]
    public void Cancel_PutsStockBack(OrderStatus from)
    {
        var product = TestData.Product(stock: 10);
        var order = new Order(Guid.NewGuid(), TestData.Customer(), DateTimeOffset.UnixEpoch);
        order.TryAddLine(product, 3);
        if (from == OrderStatus.Paid)
        {
            order.TryChangeStatus(OrderStatus.Paid);
        }

        Assert.True(order.TryChangeStatus(OrderStatus.Cancelled));
        Assert.Equal(10, product.Stock);
    }

    [Fact]
    public void Ship_DoesNotChangeStock()
    {
        var product = TestData.Product(stock: 10);
        var order = new Order(Guid.NewGuid(), TestData.Customer(), DateTimeOffset.UnixEpoch);
        order.TryAddLine(product, 3);

        order.TryChangeStatus(OrderStatus.Paid);
        order.TryChangeStatus(OrderStatus.Shipped);

        Assert.Equal(7, product.Stock);
    }
}
