using System.Globalization;
using Orders.Api.Infrastructure;

namespace Orders.UnitTests;

public class MoneyAttributeTests
{
    private static readonly MoneyAttribute Money = new();

    private static decimal Parse(string amount) => decimal.Parse(amount, CultureInfo.InvariantCulture);

    [Fact]
    public void Null_IsValid() => Assert.True(Money.IsValid(null));

    [Theory]
    [InlineData("0.01")]
    [InlineData("1")]
    [InlineData("19.99")]
    [InlineData("999999.99")]
    [InlineData("1000000")]
    public void ValidAmounts_AreAccepted(string amount) =>
        Assert.True(Money.IsValid(Parse(amount)));

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("-5")]
    [InlineData("1000000.01")]
    [InlineData("1000001")]
    [InlineData("1.001")]
    [InlineData("0.005")]
    public void InvalidAmounts_AreRejected(string amount) =>
        Assert.False(Money.IsValid(Parse(amount)));

    [Fact]
    public void NonDecimalValue_IsRejected()
    {
        Assert.False(Money.IsValid("10"));
        Assert.False(Money.IsValid(10));
    }

    [Fact]
    public void ErrorMessage_NamesTheFieldAndTheRules() =>
        Assert.Equal(
            "The Price field must be greater than 0, at most 1000000, with at most 2 decimal places.",
            Money.FormatErrorMessage("Price"));
}
