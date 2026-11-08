namespace Orders.LoadGenerator;

/// <summary>One kind of user journey, how many HTTP requests it makes, and its share of the traffic.</summary>
internal sealed record Journey(string Name, int RequestsPerRun, double ShareOfRequests);

/// <summary>A stretch of time at a constant request rate.</summary>
internal sealed record Phase(TimeSpan Duration, double RequestsPerSecond);

/// <summary>What a scenario does: phases of total request rate, split across the journeys.</summary>
internal static class LoadPlan
{
    public const double NormalRps = 5;
    public const double PeakRps = 25;
    public const double SpikeRps = 60;
    public static readonly TimeSpan SpikeLength = TimeSpan.FromSeconds(60);

    public static readonly IReadOnlyList<Journey> Journeys =
    [
        new("browse-products", 2, 0.30),  // list the catalog, open a product
        new("search", 1, 0.28),           // product words, people's names, order id prefixes
        new("view-orders", 2, 0.22),      // a page of orders (by status or customer), open one
        new("create-order", 1, 0.12),     // a real customer and 1-3 real products
        new("advance-order", 2, 0.08),    // look up, then pay, ship, deliver or cancel an order this run created
    ];

    public static IReadOnlyList<Phase> Phases(LoadScenario scenario, TimeSpan duration)
    {
        switch (scenario)
        {
            case LoadScenario.Peak:
                return [new(duration, PeakRps)];
            case LoadScenario.Spike:
                var burst = duration < SpikeLength * 2 ? duration / 2 : SpikeLength;
                var before = (duration - burst) / 2;
                return [new(before, NormalRps), new(burst, SpikeRps), new(duration - before - burst, NormalRps)];
            default:
                return [new(duration, NormalRps)];
        }
    }

    /// <summary>Journeys per second for <paramref name="journey"/> so that its requests are its share of <paramref name="rps"/>.</summary>
    public static double JourneysPerSecond(Journey journey, double rps) => rps * journey.ShareOfRequests / journey.RequestsPerRun;
}
