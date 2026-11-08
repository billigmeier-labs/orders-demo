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
    [InlineData("water bottle", "water:* & bottle:*")]
    [InlineData("  water   bottle  ", "water:* & bottle:*")]
    [InlineData("ada\tlovelace", "ada:* & lovelace:*")]
    [InlineData("a&b | c !(d)", "ab:* & c:* & d:*")]
    public void PrefixQuery_DropsTsQuerySyntaxAndMatchesPrefix(string term, string expected) =>
        Assert.Equal(expected, SearchV2.PrefixQuery(term));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("&|!()")]
    [InlineData("- & -")]
    public void PrefixQuery_NoSearchableWords_IsEmpty(string term) =>
        Assert.Equal(string.Empty, SearchV2.PrefixQuery(term));
}
