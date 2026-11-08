using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Orders.Api.Features.Platform;

namespace Orders.Api.Infrastructure;

internal sealed class AppJsonFormatterOptions : ConsoleFormatterOptions
{
    public string AppVersion { get; set; } = string.Empty;
    public string AppCommit { get; set; } = string.Empty;
    public string AppEnvironment { get; set; } = string.Empty;
}

/// <summary>
/// One JSON object per line, like the built-in JSON console formatter, plus <c>AppVersion</c>, <c>AppCommit</c>
/// and <c>AppEnvironment</c> on every line (startup included) and the trace id when there is one, so CloudWatch
/// Logs Insights can filter by release and follow a request.
/// </summary>
internal sealed class AppJsonFormatter : ConsoleFormatter, IDisposable
{
    public const string FormatterName = "app-json";

    private readonly IDisposable? _reload;
    private AppJsonFormatterOptions _options;

    public AppJsonFormatter(IOptionsMonitor<AppJsonFormatterOptions> options)
        : base(FormatterName)
    {
        _options = options.CurrentValue;
        _reload = options.OnChange(o => _options = o);
    }

    public void Dispose() => _reload?.Dispose();

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null)
        {
            return;
        }

        var options = _options;
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("Timestamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("LogLevel", logEntry.LogLevel.ToString());
            writer.WriteString("Category", logEntry.Category);
            writer.WriteNumber("EventId", logEntry.EventId.Id);
            if (logEntry.EventId.Name is { } eventName)
            {
                writer.WriteString("EventName", eventName);
            }

            writer.WriteString("Message", message);
            if (logEntry.Exception is { } exception)
            {
                writer.WriteString("Exception", exception.ToString());
            }

            writer.WriteString("AppVersion", options.AppVersion);
            writer.WriteString("AppCommit", options.AppCommit);
            writer.WriteString("AppEnvironment", options.AppEnvironment);
            if (Activity.Current is { } activity)
            {
                writer.WriteString("TraceId", activity.TraceId.ToHexString());
                writer.WriteString("SpanId", activity.SpanId.ToHexString());
            }

            if (logEntry.State is IReadOnlyList<KeyValuePair<string, object?>> state)
            {
                writer.WriteStartObject("State");
                foreach (var (key, value) in state)
                {
                    WriteValue(writer, key == "{OriginalFormat}" ? "OriginalFormat" : key, value);
                }

                writer.WriteEndObject();
            }

            if (options.IncludeScopes && scopeProvider is not null)
            {
                writer.WriteStartArray("Scopes");
                scopeProvider.ForEachScope(
                    static (scope, w) =>
                    {
                        if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                        {
                            w.WriteStartObject();
                            foreach (var (key, value) in pairs)
                            {
                                WriteValue(w, key, value);
                            }

                            w.WriteEndObject();
                        }
                        else
                        {
                            w.WriteStringValue(Convert.ToString(scope, CultureInfo.InvariantCulture));
                        }
                    },
                    writer);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        textWriter.Write(Encoding.UTF8.GetString(buffer.WrittenSpan));
        textWriter.Write(Environment.NewLine);
    }

    private static void WriteValue(Utf8JsonWriter writer, string key, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(key);
                break;
            case bool b:
                writer.WriteBoolean(key, b);
                break;
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                writer.WriteNumber(key, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case double d when double.IsFinite(d):
                writer.WriteNumber(key, d);
                break;
            case float f when float.IsFinite(f):
                writer.WriteNumber(key, f);
                break;
            case decimal m:
                writer.WriteNumber(key, m);
                break;
            default:
                writer.WriteString(key, Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}

internal static class LoggingSetup
{
    /// <summary>JSON console logs (<see cref="AppJsonFormatter"/>) carrying the app version, commit and environment.</summary>
    public static ILoggingBuilder AddAppJsonConsole(this ILoggingBuilder logging, AppInfo info)
    {
        logging.ClearProviders();
        logging.AddConsole(options => options.FormatterName = AppJsonFormatter.FormatterName);
        logging.AddConsoleFormatter<AppJsonFormatter, AppJsonFormatterOptions>(options =>
        {
            options.IncludeScopes = true;
            options.AppVersion = info.Version;
            options.AppCommit = info.Commit;
            options.AppEnvironment = info.Environment;
        });
        return logging;
    }
}
