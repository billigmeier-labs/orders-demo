using Microsoft.Extensions.Logging;
using Orders.Api.Infrastructure;

namespace Orders.UnitTests;

/// <summary>Flags set by the test: <c>new FakeFeatureFlags().Set("faultFailOrders", true, percent: 50)</c>.</summary>
internal sealed class FakeFeatureFlags : IFeatureFlags
{
    private readonly Dictionary<string, FeatureFlag> _flags = new(StringComparer.Ordinal);

    public FakeFeatureFlags Set(string flag, bool enabled, double? percent = null)
    {
        var attributes = new Dictionary<string, string>();
        if (percent is not null)
        {
            attributes["percent"] = percent.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        _flags[flag] = new FeatureFlag(enabled, attributes);
        return this;
    }

    public FeatureFlag GetFlag(string flag) => _flags.GetValueOrDefault(flag) ?? FeatureFlag.Off;
}

/// <summary>Keeps what was logged so tests can assert on it.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, EventId EventId, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, eventId, formatter(state, exception), state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
}
