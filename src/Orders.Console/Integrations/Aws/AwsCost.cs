using System.Collections.Concurrent;
using System.Globalization;
using Amazon.CostExplorer;
using Amazon.CostExplorer.Model;

namespace Orders.Console.Integrations.Aws;

/// <summary>
/// Unblended cost for resources tagged project=orders-demo: month to date and today, and per demo run
/// (tag demo-run=&lt;N&gt;). Cost Explorer charges per request and lags by hours, so answers are kept for an hour.
/// </summary>
public sealed class AwsCost(IAmazonCostExplorer costExplorer, ConsoleOptions options, TimeProvider time) : ICostService
{
    public const string RunTag = "demo-run";
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, object Value)> _cache = new(StringComparer.Ordinal);

    public Task<CostSummary> GetCostAsync(CancellationToken ct) => CachedAsync("month", async () =>
    {
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var first = new DateOnly(today.Year, today.Month, 1);
        var response = await costExplorer.GetCostAndUsageAsync(new GetCostAndUsageRequest
        {
            TimePeriod = new DateInterval { Start = Day(first), End = Day(today.AddDays(1)) },
            Granularity = Granularity.DAILY,
            Metrics = ["UnblendedCost"],
            Filter = ProjectFilter(),
        }, ct);

        decimal total = 0, todays = 0;
        var currency = "USD";
        foreach (var day in response.ResultsByTime ?? [])
        {
            if (Amount(day) is not { } amount)
            {
                continue;
            }

            currency = day.Total["UnblendedCost"].Unit ?? currency;
            total += amount;
            if (day.TimePeriod?.Start == Day(today))
            {
                todays = amount;
            }
        }

        return new CostSummary(Math.Round(total, 2), Math.Round(todays, 2), currency, now);
    }, ct);

    /// <summary>Everything tagged with this run since the start of last month (a run lasts hours, not months).</summary>
    public Task<decimal> GetRunCostAsync(string run, CancellationToken ct) => CachedAsync("run:" + run, async () =>
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var start = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var response = await costExplorer.GetCostAndUsageAsync(new GetCostAndUsageRequest
        {
            TimePeriod = new DateInterval { Start = Day(start), End = Day(today.AddDays(1)) },
            Granularity = Granularity.MONTHLY,
            Metrics = ["UnblendedCost"],
            Filter = new Expression
            {
                And = [ProjectFilter(), new Expression { Tags = new TagValues { Key = RunTag, Values = [run] } }],
            },
        }, ct);
        return Math.Round((response.ResultsByTime ?? []).Select(Amount).OfType<decimal>().Sum(), 2);
    }, ct);

    private Expression ProjectFilter() => new() { Tags = new TagValues { Key = "project", Values = [options.CostProjectTag] } };

    private static decimal? Amount(ResultByTime result) =>
        result.Total is not null && result.Total.TryGetValue("UnblendedCost", out var metric)
            ? decimal.Parse(metric.Amount ?? "0", CultureInfo.InvariantCulture)
            : null;

    private async Task<T> CachedAsync<T>(string key, Func<Task<T>> read, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(key, out var hit) && hit.Until > now && hit.Value is T value)
        {
            return value;
        }

        var fresh = await read();
        _cache[key] = (now + CacheFor, fresh!);
        return fresh;
    }

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
