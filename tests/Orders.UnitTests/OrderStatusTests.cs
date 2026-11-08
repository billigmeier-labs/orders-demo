using Orders.Api.Domain;

namespace Orders.UnitTests;

public class OrderStatusTests
{
    private static readonly (OrderStatus From, OrderStatus To)[] AllowedTransitions =
    [
        (OrderStatus.Pending, OrderStatus.Paid),
        (OrderStatus.Paid, OrderStatus.Shipped),
        (OrderStatus.Shipped, OrderStatus.Delivered),
        (OrderStatus.Pending, OrderStatus.Cancelled),
        (OrderStatus.Paid, OrderStatus.Cancelled),
    ];

    public static TheoryData<OrderStatus, OrderStatus> Allowed() => [.. AllowedTransitions];

    public static TheoryData<OrderStatus, OrderStatus> AllOthers() =>
    [
        .. from f in Enum.GetValues<OrderStatus>()
           from t in Enum.GetValues<OrderStatus>()
           where !AllowedTransitions.Contains((f, t))
           select (f, t),
    ];

    [Theory]
    [MemberData(nameof(Allowed))]
    public void CanChange_AllowedTransition_ReturnsTrue(OrderStatus from, OrderStatus to) =>
        Assert.True(OrderStatusRules.CanChange(from, to));

    [Theory]
    [MemberData(nameof(AllOthers))]
    public void CanChange_AnyOtherTransition_ReturnsFalse(OrderStatus from, OrderStatus to) =>
        Assert.False(OrderStatusRules.CanChange(from, to));

    [Theory]
    [MemberData(nameof(Allowed))]
    public void TryChangeStatus_AllowedTransition_UpdatesStatus(OrderStatus from, OrderStatus to)
    {
        var order = TestData.Order(from);

        Assert.True(order.TryChangeStatus(to));
        Assert.Equal(to, order.Status);
    }

    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Shipped)]
    [InlineData(OrderStatus.Delivered, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Paid)]
    [InlineData(OrderStatus.Paid, OrderStatus.Paid)]
    public void TryChangeStatus_DisallowedTransition_LeavesStatus(OrderStatus from, OrderStatus to)
    {
        var order = TestData.Order(from);

        Assert.False(order.TryChangeStatus(to));
        Assert.Equal(from, order.Status);
    }

    [Fact]
    public void NewOrder_IsPending() =>
        Assert.Equal(OrderStatus.Pending, TestData.Order().Status);
}
