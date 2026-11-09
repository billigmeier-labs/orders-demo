using Microsoft.Extensions.Time.Testing;
using Orders.Console.Integrations;
using Orders.Console.Integrations.Aws;
using Orders.Console.Integrations.Fakes;

namespace Orders.Console.Tests;

public sealed class DeployCommitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("983889169993.dkr.ecr.us-east-1.amazonaws.com/orders-api:3f2c9a1b4d5e6f708192a3b4c5d6e7f809112233", "3f2c9a1b4d5e6f708192a3b4c5d6e7f809112233")]
    [InlineData("localhost:5000/orders-api:a3f9c21", "a3f9c21")]
    [InlineData("983889169993.dkr.ecr.us-east-1.amazonaws.com/orders-api:pr-42", null)]
    [InlineData("983889169993.dkr.ecr.us-east-1.amazonaws.com/orders-api:latest", null)]
    [InlineData("localhost:5000/orders-api", null)]
    [InlineData("orders-api@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", null)]
    [InlineData(null, null)]
    public void Deploy_commit_is_the_image_tag_when_it_is_a_sha(string? image, string? expected)
    {
        Assert.Equal(expected, AwsDeployments.CommitFromImage(image));
    }

    [Fact]
    public void Deploy_short_commit_is_seven_characters()
    {
        var d = new DeployRecord("prod", "orders-demo-api-prod:31", "completed", DateTimeOffset.UnixEpoch, null, "3f2c9a1b4d5e6f708192a3b4c5d6e7f809112233");
        Assert.Equal("3f2c9a1", d.ShortCommit);
        Assert.Null((d with { Commit = null }).ShortCommit);
    }

    [Fact]
    public async Task Fake_deploys_carry_their_commit()
    {
        var world = new FakeWorld(new FakeTimeProvider(), new ConsoleOptions());
        Assert.All(await world.GetRecentDeploysAsync(Ct), d => Assert.Matches("^[0-9a-f]{7,40}$", d.Commit));
    }
}
