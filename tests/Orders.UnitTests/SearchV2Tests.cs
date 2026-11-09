using Orders.Api.Features.Search;

namespace Orders.UnitTests;

public class SearchV2Tests
{
    [Theory]
    [InlineData("lam", "lam:*")]
    [InlineData("sku-0001", "sku-0001:*")]
    [InlineData("o'brien", "obrien:*")]
    [InlineData("a&b|c!(d):*", "abcd:*")]
    [InlineData(@"back\slash<->", "backslash-:*")]
    public void PrefixQuery_DropsTsQuerySyntaxAndMatchesPrefix(string term, string expected) =>
        Assert.Equal(expected, SearchV2.PrefixQuery(term));
}
