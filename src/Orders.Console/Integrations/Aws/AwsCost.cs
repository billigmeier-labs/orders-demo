using System.Globalization;
using Amazon.CostExplorer;
using Amazon.CostExplorer.Model;

namespace Orders.Console.Integrations.Aws;

/// <summary>Month-to-date and today's unblended cost for resources tagged project=orders-demo.</summary>
public sealed class AwsCost(IAmazonCostExplorer costExplorer, ConsoleOptions options, TimeProvider time) : ICostService
{
    public async Task<CostSummary> GetCostAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var first = new DateOnly(today.Year, today.Month, 1);
        var response = await costExplorer.GetCostAndUsageAsync(new GetCostAndUsageRequest
        {
            TimePeriod = new DateInterval { Start = Day(first), End = Day(today.AddDays(1)) },
            Granularity = Granularity.DAILY,
            Metrics = ["UnblendedCost"],
            Filter = new Expression { Tags = new TagValues { Key = "project", Values = [options.CostProjectTag] } },
        }, ct);

        decimal total = 0, todays = 0;
        var currency = "USD";
        foreach (var day in response.ResultsByTime ?? [])
        {
            if (day.Total is null || !day.Total.TryGetValue("UnblendedCost", out var metric))
            {
                continue;
            }

            var amount = decimal.Parse(metric.Amount ?? "0", CultureInfo.InvariantCulture);
            currency = metric.Unit ?? currency;
            total += amount;
            if (day.TimePeriod?.Start == Day(today))
            {
                todays = amount;
            }
        }

        return new CostSummary(Math.Round(total, 2), Math.Round(todays, 2), currency, now);
    }

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
