using Microsoft.Extensions.Time.Testing;
using Orders.Console.Integrations.Fakes;
using Orders.Console.Status;

namespace Orders.Console.Tests;

public sealed class ScoreboardSnapshotTests
{
    private static readonly string[] WithTargets = ["ci-p90", "pr-size", "pr-to-merge", "merge-to-prod", "change-failure", "agent-clean", "review-time", "mutation"];
    private static readonly string[] WithoutTargets = ["queue-wait", "deploys", "rollbacks", "flaky", "run-cost", "cost", "ci-p50"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Showing_the_scoreboard_saves_this_runs_snapshot_and_past_runs_read_it_back()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 9, 15, 0, 0, TimeSpan.Zero));
        var world = new FakeWorld(time, new ConsoleOptions());
        var scoreboard = new ScoreboardService(world, world, world, new ConsoleOptions(), time);

        var shown = await scoreboard.ShowAsync(Ct);

        Assert.Equal(FakeWorld.DemoRun, shown.Board.Run);
        Assert.Equal($"s3://{FakeWorld.MetricsBucket}/runs/{FakeWorld.DemoRun}.json", shown.Saved!.Location);
        Assert.Null(shown.SaveError);
        Assert.Same(shown, scoreboard.Latest);
        // Every measure the spec's Goal table sets a target for is shown against it.
        foreach (var id in WithTargets)
        {
            Assert.NotNull(shown.Board.Row(id)?.Target);
        }

        Assert.All(WithoutTargets, id => Assert.NotNull(shown.Board.Row(id)));
        Assert.NotEmpty(shown.Board.Jobs);

        var past = await scoreboard.PastRunsAsync(Ct);
        Assert.Equal([FakeWorld.DemoRun, "6", "5"], past.Select(b => b.Run));
        Assert.Equal(shown.Board.Row("ci-p90")!.Value, past[0].Row("ci-p90")!.Value);
        Assert.Equal(shown.Board.Jobs.Count, past[0].Jobs.Count);
    }
}
