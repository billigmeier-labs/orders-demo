using Amazon.ECS;
using Orders.Console.Integrations.Aws;

namespace Orders.Console.Tests;

public sealed class LoadGeneratorTests
{
    [Fact]
    public void Loadgen_task_is_tagged_with_the_project_and_as_loadgen_so_cost_counts_it_and_the_console_may_stop_it()
    {
        var generator = new AwsLoadGenerator(null!, new ConsoleOptions { LoadgenSubnets = ["subnet-1"] });

        var request = generator.RunTaskRequestFor("steady", 10);

        Assert.Equal(
            new[] { ("project", "orders-demo"), ("component", "loadgen") },
            request.Tags.Select(t => (t.Key, t.Value)));
        Assert.Equal(AwsLoadGenerator.StartedBy, request.StartedBy);
        Assert.Equal(LaunchType.FARGATE, request.LaunchType);
        var env = Assert.Single(request.Overrides.ContainerOverrides).Environment.ToDictionary(e => e.Name, e => e.Value);
        Assert.Equal("steady", env["SCENARIO"]);
        Assert.Equal("10", env["DURATION_MINUTES"]);
    }

    [Fact]
    public void Loadgen_task_carries_the_demo_run_when_it_is_known()
    {
        var generator = new AwsLoadGenerator(null!, new ConsoleOptions { LoadgenSubnets = ["subnet-1"] });

        var request = generator.RunTaskRequestFor("normal", 5, "7");

        Assert.Contains(request.Tags, t => t.Key == AwsLoadGenerator.RunTagKey && t.Value == "7");
    }
}
