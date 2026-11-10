using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Orders.Api.Features.Platform;
using Orders.Api.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Orders.UnitTests;

public class LoggingTests
{
    private static readonly AppInfo Info = new("1.2.3", "abc1234", "staging");

    private readonly CapturingSink _sink = new();

    private Logger Logger() => LoggingSetup.Configure(new LoggerConfiguration(), Info).WriteTo.Sink(_sink).CreateLogger();

    [Fact]
    public void JsonLine_HasTheReleaseTheMessageAndTheProperties()
    {
        using (var logger = Logger())
        {
            logger.ForContext("SourceContext", "Test").Information("Request {Method} {Path}", "GET", "/api/orders");
        }

        using var json = JsonDocument.Parse(_sink.Json.Single());
        var root = json.RootElement;
        Assert.Equal("1.2.3", root.GetProperty("AppVersion").GetString());
        Assert.Equal("abc1234", root.GetProperty("AppCommit").GetString());
        Assert.Equal("staging", root.GetProperty("AppEnvironment").GetString());
        Assert.Equal("Request \"GET\" \"/api/orders\"", root.GetProperty("@m").GetString());
        Assert.Equal("/api/orders", root.GetProperty("Path").GetString());
        Assert.Equal("Test", root.GetProperty("SourceContext").GetString());
        Assert.False(root.TryGetProperty("@x", out _));
        Assert.DoesNotContain('\n', _sink.Json.Single());
    }

    [Fact]
    public void Exception_IsOneLineWithTheStack()
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

        using (var logger = Logger())
        {
            logger.Error(thrown, "Failed");
        }

        var line = _sink.Json.Single();
        Assert.DoesNotContain('\n', line);
        using var json = JsonDocument.Parse(line);
        Assert.Equal("Error", json.RootElement.GetProperty("@l").GetString());
        var exception = json.RootElement.GetProperty("@x").GetString();
        Assert.Contains("InvalidOperationException: boom", exception, StringComparison.Ordinal);
        Assert.Contains(nameof(Exception_IsOneLineWithTheStack), exception, StringComparison.Ordinal);
    }

    [Fact]
    public void EfCoreCommandFailures_AreNotLogged()
    {
        using (var logger = Logger())
        {
            logger.ForContext("SourceContext", "Microsoft.EntityFrameworkCore.Database.Command").Error("Failed executing DbCommand");
            logger.ForContext("SourceContext", "Microsoft.AspNetCore.Routing.EndpointMiddleware").Information("Executing endpoint");
        }

        Assert.Empty(_sink.Events);
    }

    [Fact]
    public void DuringAnActivity_TraceIdAndSpanIdAreProperties()
    {
        using var activity = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start();
        using (var logger = Logger())
        {
            logger.Information("inside");
        }

        using var json = JsonDocument.Parse(_sink.Json.Single());
        Assert.Equal(activity.TraceId.ToHexString(), json.RootElement.GetProperty("TraceId").GetString());
        Assert.Equal(activity.SpanId.ToHexString(), json.RootElement.GetProperty("SpanId").GetString());
    }

    [Theory]
    [InlineData(null, "none")]
    [InlineData("  ", "none")]
    [InlineData("create-order", "create-order")]
    [InlineData("Search", "search")]
    [InlineData("search?q=secret", "invalid")]
    [InlineData("a b", "invalid")]
    [InlineData("01234567890123456789012345678901234567890", "invalid")]
    public void Journey_IsAShortSafeToken(string? header, string expected) =>
        Assert.Equal(expected, LoggingSetup.Journey(header));

    [Fact]
    public void FlagValues_ListEveryFlagWithPercentForFaults()
    {
        var flags = new FakeFeatureFlags().Set(Flags.SearchV2, true).Set(Flags.FaultFailOrders, true, percent: 35);

        var values = LoggingSetup.FlagValues(flags);

        Assert.Equal(Flags.All.Order(StringComparer.Ordinal), values.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("on", values[Flags.SearchV2]);
        Assert.Equal("on 35%", values[Flags.FaultFailOrders]);
        Assert.Equal("off", values[Flags.FaultSlowSearch]);
    }

    [Fact]
    public async Task RequestContext_IsOnEveryLineLoggedDuringTheRequest()
    {
        using var logger = Logger();
        var flags = new FakeFeatureFlags().Set(Flags.SearchV2, true);
        var middleware = new RequestLogContextMiddleware(_ => { logger.Information("handling"); return Task.CompletedTask; }, flags);
        var context = new DefaultHttpContext { TraceIdentifier = "0HN8V1K2Q3R4S:00000001" };
        context.Request.Headers[LoggingSetup.JourneyHeader] = "view-orders";

        await middleware.InvokeAsync(context);
        logger.Information("after");

        var during = _sink.Events[0];
        Assert.Equal("\"view-orders\"", during.Properties["Journey"].ToString());
        Assert.Equal("\"0HN8V1K2Q3R4S:00000001\"", during.Properties["RequestId"].ToString());
        using var json = JsonDocument.Parse(_sink.Json[0]);
        Assert.Equal("on", json.RootElement.GetProperty("Flags").GetProperty(Flags.SearchV2).GetString());
        Assert.False(_sink.Events[1].Properties.ContainsKey("Journey"));
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public List<string> Json { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            Events.Add(logEvent);
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            LoggingSetup.JsonFormatter.Format(logEvent, writer);
            Json.Add(writer.ToString().TrimEnd('\r', '\n'));
        }
    }
}
