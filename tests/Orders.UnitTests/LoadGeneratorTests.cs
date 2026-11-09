using Orders.LoadGenerator;

namespace Orders.UnitTests;

public class LoadGeneratorTests
{
    private static LoadSettings Settings(params (string Name, string Value)[] env) =>
        LoadSettings.From(name => env.FirstOrDefault(e => e.Name == name).Value);

    [Theory]
    [InlineData(0, 100)]
    [InlineData(19, 81)]
    [InlineData(20, 0)]
    [InlineData(500, 0)]
    public void Restock_QuantityFor_TopsUpProductsBelowTwenty(int stock, int expected) =>
        Assert.Equal(expected, Restock.QuantityFor(stock));

    [Fact]
    public void Settings_Defaults_NormalForFiveMinutesAgainstLocalhost()
    {
        var settings = Settings();

        Assert.Equal(new Uri("http://localhost:8080/"), settings.TargetBaseUrl);
        Assert.Equal(LoadScenario.Normal, settings.Scenario);
        Assert.Equal(TimeSpan.FromMinutes(5), settings.Duration);
    }

    [Fact]
    public void Settings_FromEnvironment()
    {
        var settings = Settings(("TARGET_BASE_URL", "https://staging.demo.joshb.app"), ("SCENARIO", "Spike"), ("DURATION_MINUTES", "2.5"));

        Assert.Equal(new Uri("https://staging.demo.joshb.app/"), settings.TargetBaseUrl);
        Assert.Equal(LoadScenario.Spike, settings.Scenario);
        Assert.Equal(TimeSpan.FromSeconds(150), settings.Duration);
    }

    [Fact]
    public void Settings_SoakDefaultsToThirtyMinutes() =>
        Assert.Equal(TimeSpan.FromMinutes(30), Settings(("SCENARIO", "soak")).Duration);

    [Theory]
    [InlineData("SCENARIO", "chaos")]
    [InlineData("SCENARIO", "7")]
    [InlineData("DURATION_MINUTES", "0")]
    [InlineData("DURATION_MINUTES", "soon")]
    [InlineData("TARGET_BASE_URL", "ftp://example.com")]
    [InlineData("TARGET_BASE_URL", "not a url")]
    public void Settings_Invalid_Throws(string name, string value) =>
        Assert.Throws<ArgumentException>(() => Settings((name, value)));

    [Theory]
    [InlineData(LoadScenario.Normal, 5)]
    [InlineData(LoadScenario.Soak, 5)]
    [InlineData(LoadScenario.Peak, 25)]
    public void Phases_SteadyScenarios_OneRateForTheWholeRun(LoadScenario scenario, double rps)
    {
        var phase = Assert.Single(LoadPlan.Phases(scenario, TimeSpan.FromMinutes(10)));

        Assert.Equal(new Phase(TimeSpan.FromMinutes(10), rps), phase);
    }

    [Fact]
    public void Phases_Spike_SixtySecondBurstInTheMiddle()
    {
        var phases = LoadPlan.Phases(LoadScenario.Spike, TimeSpan.FromMinutes(5));

        Assert.Equal(
            [new(TimeSpan.FromSeconds(120), 5), new(TimeSpan.FromSeconds(60), 60), new(TimeSpan.FromSeconds(120), 5)],
            phases);
    }

    [Fact]
    public void Phases_ShortSpike_BurstIsHalfTheRun()
    {
        var phases = LoadPlan.Phases(LoadScenario.Spike, TimeSpan.FromSeconds(60));

        Assert.Equal(TimeSpan.FromSeconds(30), phases[1].Duration);
        Assert.Equal(TimeSpan.FromSeconds(60), phases.Aggregate(TimeSpan.Zero, (sum, p) => sum + p.Duration));
    }

    [Fact]
    public void Journeys_SharesAddUpAndRatesHitTheTarget()
    {
        Assert.Equal(1.0, LoadPlan.Journeys.Sum(j => j.ShareOfRequests), 6);
        var rps = LoadPlan.Journeys.Sum(j => LoadPlan.JourneysPerSecond(j, LoadPlan.NormalRps) * j.RequestsPerRun);
        Assert.Equal(LoadPlan.NormalRps, rps, 6);
    }

    [Theory]
    [InlineData("Pending", 0.9, "Paid")]
    [InlineData("Pending", 0.1, "Cancelled")]
    [InlineData("Paid", 0.9, "Shipped")]
    [InlineData("Paid", 0.1, "Cancelled")]
    [InlineData("Shipped", 0.1, "Delivered")]
    [InlineData("Delivered", 0.5, null)]
    [InlineData("Cancelled", 0.5, null)]
    public void NextStatus_FollowsTheLifecycle(string status, double roll, string? expected) =>
        Assert.Equal(expected, Journeys.NextStatus(status, roll));

    [Fact]
    public void SearchTerms_AQuarterAreMultiWord()
    {
        var catalog = new Catalog
        {
            Customers = [new Customer(Guid.NewGuid(), "Ada Lovelace")],
            Products = [new Product(Guid.NewGuid(), "SKU-0001", "Classic Backpack", 10)],
            Orders = [new OrderSummary(Guid.NewGuid(), Guid.NewGuid(), "Pending")],
        };
        var journeys = new Journeys(new OrdersApi(new HttpClient(), new LoadStats()), catalog, new Random(1));

        var terms = Enumerable.Range(0, 4000).Select(_ => journeys.SearchTerm()).ToList();

        var multiWord = terms.Count(t => t.Contains(' ', StringComparison.Ordinal)) / (double)terms.Count;
        Assert.InRange(multiWord, 0.22, 0.28);
        Assert.Contains(terms, t => t == catalog.Orders[0].Id.ToString()[..8]);
    }

    [Fact]
    public void Stats_CountsFailuresAsFiveHundredsAndNoResponse()
    {
        var stats = new LoadStats();
        stats.Record("GET /search", 200, TimeSpan.FromMilliseconds(5));
        stats.Record("GET /search", 409, TimeSpan.FromMilliseconds(5));
        stats.Record("GET /search", 500, TimeSpan.FromMilliseconds(5));
        stats.Record("GET /search", 0, TimeSpan.FromMilliseconds(5), "HttpRequestException");

        Assert.Equal((4, 2), (stats.Total, stats.Failed));
        var summary = stats.Summary(new LoadSettings(new Uri("http://x/"), LoadScenario.Normal, TimeSpan.FromMinutes(1)), TimeSpan.FromMinutes(1));
        Assert.Contains("failed (5xx or no response) 2 = 50.00%", summary, StringComparison.Ordinal);
        Assert.Contains("GET /search", summary, StringComparison.Ordinal);
    }
}
