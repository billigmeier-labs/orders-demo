using Orders.Api.Features.Products;

namespace Orders.UnitTests;

public class LowStockThresholdTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(1_000)]
    public void ValidateThreshold_NonNegative_IsValid(int threshold) =>
        Assert.Null(ProductEndpoints.ValidateThreshold(threshold));

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ValidateThreshold_Negative_ReturnsError(int threshold) =>
        Assert.NotNull(ProductEndpoints.ValidateThreshold(threshold));

    [Fact]
    public void DefaultThreshold_Is5() => Assert.Equal(5, ProductEndpoints.DefaultLowStockThreshold);
}
