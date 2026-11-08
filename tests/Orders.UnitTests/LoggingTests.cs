using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orders.Api.Infrastructure;

namespace Orders.UnitTests;

public class LoggingTests
{
    private static readonly AppJsonFormatterOptions Options = new()
    {
        IncludeScopes = true,
        AppVersion = "1.2.3",
        AppCommit = "abc1234",
        AppEnvironment = "staging",
    };

    [Fact]
    public void Write_AddsAppFieldsMessageAndState()
    {
        var line = Format(LogLevel.Information, "Request {Method} {Path}", ["GET", "/api/orders"]);

        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        Assert.Equal("1.2.3", root.GetProperty("AppVersion").GetString());
        Assert.Equal("abc1234", root.GetProperty("AppCommit").GetString());
        Assert.Equal("staging", root.GetProperty("AppEnvironment").GetString());
        Assert.Equal("Information", root.GetProperty("LogLevel").GetString());
        Assert.Equal("Test", root.GetProperty("Category").GetString());
        Assert.Equal("Request GET /api/orders", root.GetProperty("Message").GetString());
        Assert.Equal("/api/orders", root.GetProperty("State").GetProperty("Path").GetString());
        Assert.False(root.TryGetProperty("Exception", out _));
        Assert.EndsWith(Environment.NewLine, line, StringComparison.Ordinal);
        Assert.Single(line.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Write_Exception_IncludesTheStack()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        using var json = JsonDocument.Parse(Format(LogLevel.Error, "Failed", [], thrown));

        var exception = json.RootElement.GetProperty("Exception").GetString();
        Assert.Contains("InvalidOperationException: boom", exception, StringComparison.Ordinal);
        Assert.Contains(nameof(Write_Exception_IncludesTheStack), exception, StringComparison.Ordinal);
    }

    private static string Format(LogLevel level, string template, object?[] args, Exception? exception = null)
    {
        using var formatter = new AppJsonFormatter(new StaticOptions(Options));
        var state = new TemplateState(template, args);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        formatter.Write(
            new LogEntry<TemplateState>(level, "Test", new EventId(1, "Test"), state, exception, (s, _) => s.ToString()),
            new LoggerExternalScopeProvider(),
            writer);
        return writer.ToString();
    }

    /// <summary>A minimal structured state like the one LoggerMessage produces.</summary>
    private sealed class TemplateState(string template, object?[] args) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly List<KeyValuePair<string, object?>> _pairs = [.. Names(template).Zip(args, KeyValuePair.Create), new("{OriginalFormat}", template)];

        public int Count => _pairs.Count;

        public KeyValuePair<string, object?> this[int index] => _pairs[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _pairs.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString()
        {
            var text = template;
            foreach (var (key, value) in _pairs.Take(args.Length))
            {
                text = text.Replace($"{{{key}}}", Convert.ToString(value, CultureInfo.InvariantCulture), StringComparison.Ordinal);
            }

            return text;
        }

        private static IEnumerable<string> Names(string template) =>
            System.Text.RegularExpressions.Regex.Matches(template, @"\{(\w+)\}").Select(m => m.Groups[1].Value);
    }

    private sealed class StaticOptions(AppJsonFormatterOptions value) : IOptionsMonitor<AppJsonFormatterOptions>
    {
        public AppJsonFormatterOptions CurrentValue => value;

        public AppJsonFormatterOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AppJsonFormatterOptions, string?> listener) => null;
    }
}
