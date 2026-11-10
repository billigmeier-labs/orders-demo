using Microsoft.Extensions.Time.Testing;
using Orders.Console.Integrations;
using Orders.Console.Integrations.Aws;
using Orders.Console.Integrations.Fakes;
using Orders.Console.Scenarios;

namespace Orders.Console.Tests;

public sealed class TrafficAndDeployTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Traffic_is_requests_per_minute_over_the_window_with_the_5xx_share_and_the_latest_p95()
    {
        var requests = new Dictionary<DateTimeOffset, double> { [T0] = 300, [T0.AddMinutes(1)] = 600 };
        var errors = new Dictionary<DateTimeOffset, double> { [T0.AddMinutes(1)] = 9 };
        var p95 = new Dictionary<DateTimeOffset, double> { [T0] = 0.05, [T0.AddMinutes(1)] = 0.4 };

        var traffic = AwsMonitoring.Traffic("prod", requests, errors, p95);

        Assert.Equal(900.0 / AwsMonitoring.TrafficMinutes, traffic.RequestsPerMinute);
        Assert.Equal(0.01, traffic.ErrorRate, 6);
        Assert.Equal(TimeSpan.FromMilliseconds(400), traffic.P95Latency);
        Assert.Equal(600, traffic.LatestRequests);
        Assert.Equal([T0, T0.AddMinutes(1)], traffic.Minutes.Select(m => m.At));
    }

    [Fact]
    public void No_requests_is_no_error_rate()
    {
        var traffic = AwsMonitoring.Traffic("staging", [], [], []);

        Assert.Equal((0.0, 0.0, 0.0), (traffic.RequestsPerMinute, traffic.ErrorRate, traffic.LatestRequests));
        Assert.Null(traffic.P95Latency);
    }

    [Theory]
    [InlineData("SUCCESSFUL", "succeeded")]
    [InlineData("ROLLBACK_SUCCESSFUL", "rolled back")]
    [InlineData("ROLLBACK_IN_PROGRESS", "rolling back")]
    [InlineData("IN_PROGRESS", "in progress")]
    [InlineData("STOPPED", "stopped")]
    [InlineData(null, "unknown")]
    public void Ecs_deployment_states_read_as_words(string? status, string expected) =>
        Assert.Equal(expected, AwsDeployments.State(status));

    [Fact]
    public async Task Fake_traffic_reaches_production_while_load_runs()
    {
        var time = new FakeTimeProvider(T0);
        var world = new FakeWorld(time, new ConsoleOptions { FakeStep = TimeSpan.FromSeconds(3) });
        var conditions = new ConditionEvaluator(world, world);
        Assert.False(await conditions.EvaluateAsync(ConditionEvaluator.ProdTraffic, null, Ct));

        await world.StartAsync("normal", 5, Ct);
        time.Advance(TimeSpan.FromSeconds(4));

        Assert.True(await conditions.EvaluateAsync(ConditionEvaluator.ProdTraffic, null, Ct));
        var staging = (await world.GetTrafficAsync(Ct)).Single(t => t.Environment == "staging");
        Assert.True(staging.LatestRequests < ConditionEvaluator.TrafficThreshold);
    }

    [Fact]
    public async Task Fake_guardrails_pr_fails_pr_rules_and_needs_a_code_owner()
    {
        var time = new FakeTimeProvider(T0);
        var world = new FakeWorld(time, new ConsoleOptions { FakeStep = TimeSpan.FromSeconds(3) });
        var pr = await world.OpenPullRequestAsync(new NewPullRequest(FakeWorld.GuardrailsBranch, "main", "ci: skip a test", "Closes #1"), Ct);
        var conditions = new ConditionEvaluator(world, world);

        for (var i = 0; i < 30; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await world.GetAlarmsAsync(Ct);
        }

        var details = world.Events.Where(e => e.Subject == $"pr#{pr.Number}").Select(e => e.Detail).ToList();
        Assert.Contains("PR rules: failure", details);
        Assert.Contains("ci-ok: failure", details);
        Assert.DoesNotContain(world.Events, e => e.Kind == "pr.merged" && e.Subject == $"pr#{pr.Number}");
        Assert.True(await conditions.EvaluateAsync(ConditionEvaluator.ReviewRequired, $"pr#{pr.Number}", Ct));
        Assert.False(await conditions.EvaluateAsync(ConditionEvaluator.ReviewRequired, null, Ct));
    }
}
