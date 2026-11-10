using System.Text.Json;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Orders.Console.Domain;

namespace Orders.Console.Integrations.Aws;

/// <summary>CloudWatch alarms (state and history), load balancer traffic per environment, and recent error lines from CloudWatch Logs.</summary>
public sealed class AwsMonitoring(IAmazonCloudWatch cloudWatch, IAmazonCloudWatchLogs logs, ConsoleOptions options, TimeProvider time)
    : IMonitoringService, IEventSource
{
    public string Name => "cloudwatch";

    public TimeSpan Interval => options.AwsPollInterval;

    public async Task<IReadOnlyList<AlarmState>> GetAlarmsAsync(CancellationToken ct)
    {
        var result = new List<AlarmState>();
        string? next = null;
        do
        {
            var page = await cloudWatch.DescribeAlarmsAsync(new DescribeAlarmsRequest { AlarmNamePrefix = options.AlarmPrefix, MaxRecords = 100, NextToken = next }, ct);
            foreach (var a in page.MetricAlarms ?? [])
            {
                result.Add(new AlarmState(a.AlarmName, a.StateValue?.Value ?? "INSUFFICIENT_DATA", a.StateReason, Utc(a.StateUpdatedTimestamp), AlarmUrl(a.AlarmName)));
            }

            foreach (var a in page.CompositeAlarms ?? [])
            {
                result.Add(new AlarmState(a.AlarmName, a.StateValue?.Value ?? "INSUFFICIENT_DATA", a.StateReason, Utc(a.StateUpdatedTimestamp), AlarmUrl(a.AlarmName)));
            }

            next = page.NextToken;
        }
        while (next is not null);

        return result.OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
    }

    public const int TrafficMinutes = 15;

    // The load balancer and target group of each environment, read from its alarms' dimensions.
    private (DateTimeOffset Until, IReadOnlyDictionary<string, List<Dimension>> Targets)? _targets;

    /// <summary>
    /// The last <see cref="TrafficMinutes"/> minutes of ALB metrics per environment: RequestCount,
    /// HTTPCode_Target_5XX_Count and TargetResponseTime p95, one point per minute. Each environment's
    /// LoadBalancer and TargetGroup dimensions come from its deploy alarms (alarms.tf), so no ELB API is needed.
    /// </summary>
    public async Task<IReadOnlyList<EnvironmentTraffic>> GetTrafficAsync(CancellationToken ct)
    {
        var targets = await TargetsAsync(ct);
        if (targets.Count == 0)
        {
            return [];
        }

        var queries = new List<MetricDataQuery>();
        foreach (var (env, dims) in targets)
        {
            queries.Add(Query($"r_{env}", "RequestCount", dims, stat: "Sum"));
            queries.Add(Query($"e_{env}", "HTTPCode_Target_5XX_Count", dims, stat: "Sum"));
            queries.Add(Query($"p_{env}", "TargetResponseTime", dims, stat: "p95"));
        }

        var end = Minute(time.GetUtcNow());
        var results = new Dictionary<string, Dictionary<DateTimeOffset, double>>(StringComparer.Ordinal);
        string? next = null;
        do
        {
            var page = await cloudWatch.GetMetricDataAsync(new GetMetricDataRequest
            {
                StartTime = end.AddMinutes(-TrafficMinutes).UtcDateTime,
                EndTime = end.UtcDateTime,
                MetricDataQueries = queries,
                NextToken = next,
            }, ct);
            foreach (var r in page.MetricDataResults ?? [])
            {
                var points = results.TryGetValue(r.Id, out var existing) ? existing : results[r.Id] = [];
                foreach (var (at, value) in (r.Timestamps ?? []).Zip(r.Values ?? []))
                {
                    points[Utc(at)] = value;
                }
            }

            next = page.NextToken;
        }
        while (next is not null);

        return targets.Keys.Order(StringComparer.Ordinal).Select(env => Traffic(env,
            results.GetValueOrDefault($"r_{env}") ?? [], results.GetValueOrDefault($"e_{env}") ?? [], results.GetValueOrDefault($"p_{env}") ?? [])).ToList();
    }

    internal static EnvironmentTraffic Traffic(string env, Dictionary<DateTimeOffset, double> requests, Dictionary<DateTimeOffset, double> errors, Dictionary<DateTimeOffset, double> p95)
    {
        var minutes = requests.Keys.Union(errors.Keys).Union(p95.Keys).Order()
            .Select(at => new TrafficPoint(at, requests.GetValueOrDefault(at), errors.GetValueOrDefault(at),
                p95.TryGetValue(at, out var seconds) ? TimeSpan.FromSeconds(seconds) : null))
            .ToList();
        var total = minutes.Sum(m => m.Requests);
        var failed = minutes.Sum(m => m.Errors5xx);
        return new EnvironmentTraffic(env, total / TrafficMinutes, total > 0 ? failed / total : 0,
            minutes.LastOrDefault(m => m.P95Latency is not null)?.P95Latency, minutes);
    }

    private async Task<IReadOnlyDictionary<string, List<Dimension>>> TargetsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_targets is { } hit && hit.Until > now)
        {
            return hit.Targets;
        }

        var targets = new Dictionary<string, List<Dimension>>(StringComparer.Ordinal);
        string? next = null;
        do
        {
            var page = await cloudWatch.DescribeAlarmsAsync(new DescribeAlarmsRequest { AlarmNamePrefix = options.AlarmPrefix, MaxRecords = 100, NextToken = next }, ct);
            foreach (var a in page.MetricAlarms ?? [])
            {
                var env = a.AlarmName.Contains(options.EcsProdService, StringComparison.Ordinal) ? "prod"
                    : a.AlarmName.Contains(options.EcsStagingService, StringComparison.Ordinal) ? "staging"
                    : null;
                var dims = a.Dimensions ?? [];
                if (env is not null && a.Namespace == "AWS/ApplicationELB" && !targets.ContainsKey(env)
                    && dims.Any(d => d.Name == "LoadBalancer") && dims.Any(d => d.Name == "TargetGroup"))
                {
                    targets[env] = dims.Where(d => d.Name is "LoadBalancer" or "TargetGroup").ToList();
                }
            }

            next = page.NextToken;
        }
        while (next is not null);

        _targets = (now + TimeSpan.FromMinutes(10), targets);
        return targets;
    }

    private static MetricDataQuery Query(string id, string metric, List<Dimension> dims, string stat) => new()
    {
        Id = id,
        ReturnData = true,
        MetricStat = new MetricStat
        {
            Metric = new Metric { Namespace = "AWS/ApplicationELB", MetricName = metric, Dimensions = dims },
            Period = 60,
            Stat = stat,
        },
    };

    private static DateTimeOffset Minute(DateTimeOffset t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero);

    public async Task<IReadOnlyList<LogLine>> GetRecentErrorsAsync(CancellationToken ct)
    {
        var lines = new List<LogLine>();
        string? next = null;
        var pages = 0;
        do
        {
            var page = await logs.FilterLogEventsAsync(new FilterLogEventsRequest
            {
                LogGroupName = options.ProdLogGroup,
                FilterPattern = options.ErrorLogFilter,
                StartTime = time.GetUtcNow().AddMinutes(-30).ToUnixTimeMilliseconds(),
                Limit = 100,
                NextToken = next,
            }, ct);
            lines.AddRange((page.Events ?? []).Select(e => new LogLine(
                DateTimeOffset.FromUnixTimeMilliseconds(e.Timestamp ?? 0), e.Message?.Trim() ?? "", e.LogStreamName)));
            next = page.NextToken;
        }
        while (next is not null && ++pages < 5);

        return lines.OrderByDescending(l => l.At).Take(12).ToList();
    }

    public async Task<IReadOnlyList<ConsoleEvent>> PollAsync(DateTimeOffset since, CancellationToken ct)
    {
        var page = await cloudWatch.DescribeAlarmHistoryAsync(new DescribeAlarmHistoryRequest
        {
            HistoryItemType = HistoryItemType.StateUpdate,
            StartDate = since.UtcDateTime,
            EndDate = time.GetUtcNow().UtcDateTime.AddMinutes(1),
            MaxRecords = 100,
            ScanBy = ScanBy.TimestampDescending,
        }, ct);

        var events = new List<ConsoleEvent>();
        foreach (var item in page.AlarmHistoryItems ?? [])
        {
            if (item.AlarmName is null || !item.AlarmName.StartsWith(options.AlarmPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var state = NewState(item.HistoryData);
            var kind = state switch
            {
                "ALARM" => EventKinds.AlarmAlarm,
                "OK" => EventKinds.AlarmOk,
                _ => null,
            };
            if (kind is not null)
            {
                events.Add(new ConsoleEvent(Utc(item.Timestamp), kind, item.AlarmName, item.HistorySummary, AlarmUrl(item.AlarmName)));
            }
        }

        return events;
    }

    internal static string? NewState(string? historyData)
    {
        if (string.IsNullOrEmpty(historyData))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(historyData);
            return doc.RootElement.TryGetProperty("newState", out var s) && s.TryGetProperty("stateValue", out var v) ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string AlarmUrl(string name) =>
        $"https://{options.AwsRegion}.console.aws.amazon.com/cloudwatch/home?region={options.AwsRegion}#alarmsV2:alarm/{Uri.EscapeDataString(name)}";

    private static DateTimeOffset Utc(DateTime? value) =>
        value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v.ToUniversalTime(), DateTimeKind.Utc)) : DateTimeOffset.MinValue;
}
