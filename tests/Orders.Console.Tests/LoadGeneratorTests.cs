using Amazon.ECS;
using Orders.Console.Integrations.Aws;

namespace Orders.Console.Tests;

public sealed class LoadGeneratorTests
{
    [Fact]
    public void Loadgen_task_is_tagged_with_the_project_so_cost_counts_it()
    {
        var generator = new AwsLoadGenerator(null!, new ConsoleOptions { LoadgenSubnets = ["subnet-1"] });

        var request = generator.RunTaskRequestFor("steady", 10);

        var tag = Assert.Single(request.Tags);
        Assert.Equal(("project", "orders-demo"), (tag.Key, tag.Value));
        Assert.Equal(AwsLoadGenerator.StartedBy, request.StartedBy);
        Assert.Equal(LaunchType.FARGATE, request.LaunchType);
        var env = Assert.Single(request.Overrides.ContainerOverrides).Environment.ToDictionary(e => e.Name, e => e.Value);
        Assert.Equal("steady", env["SCENARIO"]);
        Assert.Equal("10", env["DURATION_MINUTES"]);
    }
}
