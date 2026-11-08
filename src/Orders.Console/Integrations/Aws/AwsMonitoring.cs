using System.Text.Json;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Orders.Console.Domain;

namespace Orders.Console.Integrations.Aws;

/// <summary>CloudWatch alarms (state and history) and recent error lines from CloudWatch Logs.</summary>
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
