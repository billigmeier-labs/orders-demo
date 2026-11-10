using Orders.Console.Domain;
using Orders.Console.Scenarios;

namespace Orders.Console.Tests;

public sealed class MatchingAndCatalogTests
{
    [Theory]
    [InlineData("claude[bot]: feat (refs #42)", "#42", true)]
    [InlineData("claude[bot]: feat (refs #420)", "#42", false)]
    [InlineData("ci-ok: success", "ci-ok: success", true)]
    [InlineData("ci-ok: failure", "ci-ok: success", false)]
    [InlineData("needs-human", "agent", false)]
    [InlineData("AGENT", "agent", true)]
    public void ContainsToken_respects_word_boundaries(string haystack, string needle, bool expected) =>
        Assert.Equal(expected, Matching.ContainsToken(haystack, needle));

    [Fact]
    public void Fill_throws_for_unknown_variables()
    {
        var vars = new Dictionary<string, string> { ["issue"] = "7" };
        Assert.Equal("issue#7", Matching.Fill("issue#{issue}", vars));
        Assert.Throws<MissingVariableException>(() => Matching.Fill("pr#{pr}", vars));
    }

    [Fact]
    public void A_condition_with_an_unfilled_variable_never_matches()
    {
        var c = Defs.On(EventKinds.PrMerged, "pr#{pr}");
        Assert.False(Matching.Matches(c, new ConsoleEvent(DateTimeOffset.UtcNow, EventKinds.PrMerged, "pr#{pr}"), new Dictionary<string, string>()));
    }

    [Fact]
    public void Shipped_scenario_files_load_and_validate()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Scenarios", "Definitions");
        var defs = ScenarioCatalog.LoadDirectory(dir);
        var catalog = new ScenarioCatalog(defs, ScenarioActions.KnownKinds, new HashSet<string> { ConditionEvaluator.AlarmsOk, ConditionEvaluator.ProdTraffic, ConditionEvaluator.ReviewRequired });

        Assert.Equal("tour", catalog.Tour.Id);
        Assert.Equal(["issue", "triage", "agent", "checks", "preview", "queue", "staging", "production", "traffic", "monitor"], catalog.Tour.Steps.Select(s => s.Stage));
        Assert.Equal(Orders.Console.Content.TourContent.Stages.Select(s => s.Id), catalog.Tour.Steps.Select(s => s.Stage));
        // Every stage has its "Where this is running" map and an explainer.
        Assert.All(Orders.Console.Content.TourContent.Stages, s =>
        {
            Assert.True(Orders.Console.Content.TourContent.Where.ContainsKey(s.Id), s.Id);
            Assert.NotNull(Orders.Console.Content.Explainers.Find(s.Id));
        });
        // Deploy waits stay keyed to the scenario's own PR.
        Assert.All(catalog.All.SelectMany(s => s.Steps).SelectMany(s => s.Wait).Where(w => w.Kind == "deploy.succeeded"),
            w => Assert.Contains("#{pr}", w.DetailContains));
        Assert.All(catalog.All.SelectMany(s => s.Steps).Select(s => s.Explain ?? s.Stage).OfType<string>(),
            id => Assert.NotNull(Orders.Console.Content.Explainers.Find(id)));
        Assert.Equal(
            ["bad-deploy", "incident", "guardrails", "risky-change", "mutation", "scoreboard"],
            catalog.Library.Select(s => s.Id));
        Assert.All(catalog.All, s => Assert.NotNull(s.Steps[0].Action));
    }

    [Fact]
    public void Validation_reports_unknown_kinds_conditions_and_duplicates()
    {
        var bad = new ScenarioDefinition
        {
            Id = "x",
            Title = "x",
            Tour = true,
            Steps =
            [
                new StepDefinition { Id = "a", Title = "a", Action = Defs.Action("nope.action"), Wait = [Defs.On("issue.exploded")] },
                new StepDefinition { Id = "a", Title = "b", Wait = [new WaitCondition { Condition = "moon-full", Description = "moon" }] },
            ],
        };
        var errors = ScenarioCatalog.Validate([bad], ScenarioActions.KnownKinds, new HashSet<string> { "alarms-ok" });

        Assert.Contains(errors, e => e.Contains("nope.action", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("issue.exploded", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("moon-full", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'a' is used more than once", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => new ScenarioCatalog([bad], ScenarioActions.KnownKinds, new HashSet<string>()));
    }
}
