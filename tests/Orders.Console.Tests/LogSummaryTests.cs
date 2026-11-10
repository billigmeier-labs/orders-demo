using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Orders.Console.Integrations.Fakes;
using Orders.Console.Status;

namespace Orders.Console.Tests;

public sealed class LogSummaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A line as the API writes it (Orders.Api Infrastructure/Logging.cs), stack trace trimmed.
    private const string UnhandledSearchError =
        """{"Timestamp":"2026-11-09T15:04:05.1234567+00:00","LogLevel":"Error","Category":"Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware","EventId":1,"EventName":"UnhandledException","Message":"An unhandled exception has occurred while executing the request.","Exception":"Npgsql.PostgresException (0x80004005): 42601: syntax error in tsquery: \"alan turing:*\"\n   at Npgsql.Internal.NpgsqlConnector.ReadMessageLong(Boolean async)\n   at Npgsql.NpgsqlDataReader.NextResult()","AppVersion":"1.4.2","AppCommit":"a3f9c21","AppEnvironment":"prod","TraceId":"4bf92f3577b34da6a3ce929d0e0e4736","State":{"OriginalFormat":"An unhandled exception has occurred while executing the request."},"Scopes":[{"SpanId":"00f067aa0ba902b7","TraceId":"4bf92f3577b34da6a3ce929d0e0e4736"},{"ConnectionId":"0HN8V1K2Q3R4S"},{"RequestId":"0HN8V1K2Q3R4S:00000001","RequestPath":"/api/search"}]}""";

    [Fact]
    public void Log_summary_shows_the_request_path_exception_type_and_first_message_line()
    {
        Assert.Equal("/api/search — PostgresException 42601: syntax error in tsquery: \"alan turing:*\"", LogSummary.Summarize(UnhandledSearchError));
    }

    // The same error in the API's Serilog compact JSON (Orders.Api Infrastructure/Logging.cs).
    private const string CompactSearchError =
        """{"@t":"2026-11-09T15:04:05.1234567Z","@m":"An unhandled exception has occurred while executing the request.","@i":"6f8cd2f2","@l":"Error","@x":"Npgsql.PostgresException (0x80004005): 42601: syntax error in tsquery: \"alan turing:*\"\n   at Npgsql.Internal.NpgsqlConnector.ReadMessageLong(Boolean async)","@tr":"4bf92f3577b34da6a3ce929d0e0e4736","SourceContext":"Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware","RequestId":"0HN8V1K2Q3R4S:00000001","RequestPath":"/api/search","Journey":"search","Flags":{"searchV2":"on","faultSlowSearch":"off","faultFailOrders":"off"},"AppVersion":"1.4.2","AppCommit":"a3f9c21","AppEnvironment":"prod","TraceId":"4bf92f3577b34da6a3ce929d0e0e4736"}""";

    [Fact]
    public void Log_summary_reads_the_serilog_compact_format()
    {
        Assert.Equal("/api/search — PostgresException 42601: syntax error in tsquery: \"alan turing:*\"", LogSummary.Summarize(CompactSearchError));
        Assert.Equal("GET /api/orders — HTTP GET /api/orders responded 500 in 12.0 ms",
            LogSummary.Summarize("""{"@m":"HTTP GET /api/orders responded 500 in 12.0 ms","@l":"Error","RequestMethod":"GET","RequestPath":"/api/orders","StatusCode":500}"""));
    }

    [Fact]
    public void Log_summary_includes_the_method_only_when_the_log_has_it()
    {
        var line = """{"Message":"boom","Exception":"System.InvalidOperationException: Sequence contains no elements\n   at X.Y()","Scopes":[{"RequestPath":"/api/orders"}],"State":{"Method":"POST"}}""";
        Assert.Equal("POST /api/orders — InvalidOperationException Sequence contains no elements", LogSummary.Summarize(line));
    }

    [Theory]
    [InlineData("""{"LogLevel":"Error","Message":"Failed to read flags\nsecond line"}""", "Failed to read flags")]
    [InlineData("""{"level":"Warning","message":"Slow query (812 ms)","Scopes":[{"RequestPath":"/api/search"}]}""", "/api/search — Slow query (812 ms)")]
    [InlineData("""{"LogLevel":"Error","Scopes":[{"RequestPath":"/api/search"}]}""", "/api/search")]
    [InlineData("""{"LogLevel":"Error"}""", """{"LogLevel":"Error"}""")]
    [InlineData("fail: Orders.Api[0] something broke\n   at Foo()", "fail: Orders.Api[0] something broke")]
    [InlineData("{not json", "{not json")]
    [InlineData("", "(empty log line)")]
    public void Log_summary_falls_back_to_the_message_then_the_raw_line(string raw, string expected)
    {
        Assert.Equal(expected, LogSummary.Summarize(raw));
    }

    [Fact]
    public void Log_summary_is_truncated_to_one_short_line()
    {
        var summary = LogSummary.Summarize(new string('x', 5000));
        Assert.Equal(LogSummary.MaxLength, summary.Length);
        Assert.EndsWith("…", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Exception_type_without_a_message_is_just_the_short_type()
    {
        Assert.Equal("TaskCanceledException", LogSummary.Exception("System.Threading.Tasks.TaskCanceledException\n   at X()"));
        Assert.Equal("Something odd: happened here", LogSummary.Exception("Something odd: happened here"));
    }

    [Fact]
    public async Task Fake_error_lines_look_like_the_api_logs_and_summarize_to_one_line()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 15, 0, 0, TimeSpan.Zero));
        var world = new FakeWorld(time, new ConsoleOptions { FakeStep = TimeSpan.FromSeconds(3) });
        var line = Assert.Single(await world.GetRecentErrorsAsync(Ct));
        Assert.Equal("/api/search — Slow query on /api/search (812 ms)", LogSummary.Summarize(line.Message));
        Assert.NotNull(JsonNode.Parse(line.Message)?["AppCommit"]);
    }
}
