using System.Diagnostics;
using NBomber.Contracts;
using NBomber.CSharp;
using Orders.LoadGenerator;

// Load generator for the orders API. Env: TARGET_BASE_URL, SCENARIO (normal|peak|spike|soak), DURATION_MINUTES.
LoadSettings settings;
try
{
    settings = LoadSettings.From(Environment.GetEnvironmentVariable);
}
catch (ArgumentException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 2;
}

using var http = new HttpClient { BaseAddress = settings.TargetBaseUrl, Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("orders-loadgen/1.0");
var stats = new LoadStats();
var api = new OrdersApi(http, stats);

Catalog catalog;
try
{
    catalog = await Catalog.LoadAsync(api, CancellationToken.None);
}
catch (InvalidOperationException ex)
{
    await Console.Error.WriteLineAsync($"Setup against {settings.TargetBaseUrl} failed: {ex.Message}");
    return 1;
}

Console.WriteLine($"Target {settings.TargetBaseUrl}: {catalog.Customers.Count} customers, {catalog.Products.Count} products, {catalog.Orders.Count} recent orders.");
await Restock.RunAsync(api, catalog.Products, Console.WriteLine, CancellationToken.None);
foreach (var phase in LoadPlan.Phases(settings.Scenario, settings.Duration))
{
    Console.WriteLine($"  {phase.Duration.TotalSeconds:0} s at {phase.RequestsPerSecond:0} req/s");
}

var journeys = new Journeys(api, catalog, Random.Shared);
Func<CancellationToken, Task<IReadOnlyList<CallResult>>> Run(string name) => name switch
{
    Journeys.Browse => journeys.BrowseProductsAsync,
    Journeys.Search => journeys.SearchAsync,
    Journeys.ViewOrders => journeys.ViewOrdersAsync,
    Journeys.CreateOrder => journeys.CreateOrderAsync,
    Journeys.AdvanceOrder => journeys.AdvanceOrderAsync,
    _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
};

var scenarios = LoadPlan.Journeys.Select(journey =>
{
    var run = Run(journey.Name);
    return Scenario.Create(journey.Name, async context =>
        {
            var results = await run(context.ScenarioCancellationToken);
            var failed = results.Where(r => r.Failed).Select(r => (CallResult?)r).FirstOrDefault();
            return failed is { } f
                ? Response.Fail(statusCode: f.Status.ToString(System.Globalization.CultureInfo.InvariantCulture), message: f.Error ?? "server error")
                : Response.Ok(statusCode: results.Count > 0 ? results[^1].Status.ToString(System.Globalization.CultureInfo.InvariantCulture) : "skipped");
        })
        .WithoutWarmUp()
        .WithLoadSimulations([.. LoadPlan.Phases(settings.Scenario, settings.Duration)
            .Where(p => p.Duration > TimeSpan.Zero)
            .Select(p => Inject(LoadPlan.JourneysPerSecond(journey, p.RequestsPerSecond), p.Duration))]);
}).ToArray();

var timer = Stopwatch.StartNew();
NBomberRunner
    .RegisterScenarios(scenarios)
    .WithTestSuite("orders-demo")
    .WithTestName(settings.Scenario.ToString().ToLowerInvariant())
    .WithReportFolder(Path.Combine(Path.GetTempPath(), "nbomber-reports"))
    .Run();

Console.WriteLine();
Console.WriteLine(stats.Summary(settings, timer.Elapsed));
return 0;

// Arrivals spread evenly: one journey every 1/rate seconds (at least every 100 ms, several per tick above 10/s).
static LoadSimulation Inject(double journeysPerSecond, TimeSpan during)
{
    var interval = TimeSpan.FromSeconds(Math.Max(0.1, 1 / journeysPerSecond));
    var rate = Math.Max(1, (int)Math.Round(journeysPerSecond * interval.TotalSeconds));
    return Simulation.Inject(rate: rate, interval: interval, during: during);
}
