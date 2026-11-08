using Orders.Console.Integrations;
using Orders.Console.Scenarios;

namespace Orders.Console.Events;

/// <summary>
/// Polls every <see cref="IEventSource"/> on its own interval, records new events, then lets the
/// scenario engine look at them (advance steps, time out waits).
/// </summary>
public sealed partial class EventCollector(
    IEnumerable<IEventSource> sources,
    EventStore store,
    ScenarioEngine engine,
    SourceHealth health,
    ConsoleOptions options,
    TimeProvider time,
    ILogger<EventCollector> logger) : BackgroundService
{
    private static readonly TimeSpan Lookback = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var list = sources.ToList();
        var cursors = list.ToDictionary(s => s.Name, _ => time.GetUtcNow() - Lookback);
        var due = list.ToDictionary(s => s.Name, _ => DateTimeOffset.MinValue);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            foreach (var source in list.Where(s => due[s.Name] <= now))
            {
                due[source.Name] = now + source.Interval;
                try
                {
                    var polledAt = time.GetUtcNow();
                    var events = await source.PollAsync(cursors[source.Name], stoppingToken);
                    var added = store.Add(events);
                    // Keep a little overlap so late-arriving timestamps are not lost; the store de-duplicates.
                    cursors[source.Name] = polledAt - TimeSpan.FromMinutes(10);
                    health.Ok(source.Name, polledAt);
                    if (added > 0)
                    {
                        LogAdded(added, source.Name);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    health.Failed(source.Name, time.GetUtcNow(), ex.Message);
                    LogPollFailed(ex, source.Name);
                }
            }

            try
            {
                await engine.TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTickFailed(ex);
            }

            try
            {
                await Task.Delay(options.EngineTick, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Recorded {Count} new events from {Source}")]
    private partial void LogAdded(int count, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polling {Source} failed")]
    private partial void LogPollFailed(Exception ex, string source);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scenario engine tick failed")]
    private partial void LogTickFailed(Exception ex);
}

/// <summary>Last poll result per event source, shown on the Status page.</summary>
public sealed class SourceHealth
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SourceState> _states = new(StringComparer.Ordinal);

    public void Ok(string name, DateTimeOffset at)
    {
        lock (_gate)
        {
            _states[name] = new SourceState(name, true, at, null);
        }
    }

    public void Failed(string name, DateTimeOffset at, string error)
    {
        lock (_gate)
        {
            _states[name] = new SourceState(name, false, at, error);
        }
    }

    public IReadOnlyList<SourceState> All()
    {
        lock (_gate)
        {
            return _states.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        }
    }
}

public sealed record SourceState(string Name, bool Ok, DateTimeOffset At, string? Error);
