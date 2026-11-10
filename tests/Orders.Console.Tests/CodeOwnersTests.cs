using Orders.Console.Integrations;
using Orders.Console.Integrations.GitHub;

namespace Orders.Console.Tests;

public sealed class CodeOwnersTests
{
    private const string File = """
        # Paths that always need a human's approval
        /.github/                                   @jbilligmeier
        /AGENTS.md                                  @jbilligmeier
        /src/Orders.Api/Data/Migrations/            @jbilligmeier
        /tests/testconfig.json                      @jbilligmeier
        docs/*.md                                   @jbilligmeier
        """;

    [Theory]
    [InlineData(".github/workflows/ci.yml", true)]
    [InlineData("AGENTS.md", true)]
    [InlineData("src/Orders.Api/Data/Migrations/20261109_Add.cs", true)]
    [InlineData("tests/testconfig.json", true)]
    [InlineData("docs/contracts.md", true)]
    [InlineData("docs/adr/0001-x.md", false)]
    [InlineData("tests/Orders.UnitTests/OrderTotalTests.cs", false)]
    [InlineData("src/Orders.Api/Program.cs", false)]
    [InlineData("nested/AGENTS.md", false)]
    public void Matches_like_github(string path, bool owned)
    {
        Assert.Equal(owned, CodeOwners.Matches(CodeOwners.Parse(File), path));
    }

    [Theory]
    [InlineData("REVIEW_REQUIRED", false, true)]
    [InlineData(null, true, true)]
    [InlineData("APPROVED", true, false)]
    [InlineData(null, false, false)]
    public void Review_is_required_when_github_says_so_or_a_code_owned_path_is_unapproved(string? decision, bool touchesOwned, bool required)
    {
        Assert.Equal(required, new PullReviewState(1, decision, "BLOCKED", touchesOwned).ReviewRequired);
    }
}
