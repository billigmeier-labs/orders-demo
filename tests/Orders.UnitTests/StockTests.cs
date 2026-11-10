using System.ComponentModel.DataAnnotations;
using Orders.Api.Domain;
using Orders.Api.Features.Products;

namespace Orders.UnitTests;

public class StockTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(10_000, true)]
    [InlineData(10_001, false)]
    public void RestockRequest_Quantity_MustBeOneToTenThousand(int quantity, bool valid) =>
        Assert.Equal(valid, IsValid(new RestockRequest(quantity)));

    [Fact]
    public void RestockRequest_MissingQuantity_IsInvalid() =>
        Assert.False(IsValid(new RestockRequest(null)));

    [Theory]
    [InlineData(null, 5)]
    [InlineData(0, 0)]
    [InlineData(12, 12)]
    [InlineData(-1, null)]
    public void LowStockThreshold_DefaultsToFive_AndRejectsNegative(int? requested, int? expected) =>
        Assert.Equal(expected, ProductEndpoints.ResolveLowStockThreshold(requested));

    private static bool IsValid(object instance) =>
        Validator.TryValidateObject(instance, new ValidationContext(instance), null, validateAllProperties: true);

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
