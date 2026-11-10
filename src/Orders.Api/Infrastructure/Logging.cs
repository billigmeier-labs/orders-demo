using System.Diagnostics;
using System.Globalization;
using Orders.Api.Features.Platform;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Orders.Api.Infrastructure;

/// <summary>
/// Serilog, writing one compact JSON object per line outside Development (plain text in a developer's terminal).
/// Every line carries <c>AppVersion</c>, <c>AppCommit</c> and <c>AppEnvironment</c>; during a request also
/// <c>RequestId</c>, <c>Journey</c> (the load generator's <c>X-Journey</c> header), <c>Flags</c> (every flag's value)
/// and <c>TraceId</c>/<c>SpanId</c>, so CloudWatch Logs Insights can filter by release, journey or flag and follow one request.
/// </summary>
/// <remarks>
/// Field names are Serilog's compact format: <c>@t</c> time, <c>@m</c> rendered message, <c>@mt</c> template,
/// <c>@l</c> level (left out for Information), <c>@x</c> exception with its stack; everything else is a property.
/// Query strings are never logged: they are user input, and the incident agent reads these logs.
/// </remarks>
internal static class LoggingSetup
{
    public const string JourneyHeader = "X-Journey";
    public const int MaxJourneyLength = 40;

    public static WebApplicationBuilder AddAppLogging(this WebApplicationBuilder builder, AppInfo info)
    {
        var json = !builder.Environment.IsDevelopment();
        builder.Services.AddSerilog(
            (services, logger) =>
            {
                Configure(logger, info).ReadFrom.Services(services);
                if (json)
                {
                    logger.WriteTo.Console(JsonFormatter);
                }
                else
                {
                    logger.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);
                }
            },
            preserveStaticLogger: true);
        return builder;
    }

    /// <summary>One JSON object per line, message rendered (<c>@m</c>) so a person can read it as is.</summary>
    internal static readonly RenderedCompactJsonFormatter JsonFormatter = new();

    /// <summary>Levels and enrichers. Sinks are added by the caller; <c>ReadFrom.Services</c> lets tests add one too.</summary>
    internal static LoggerConfiguration Configure(LoggerConfiguration logger, AppInfo info) =>
        logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            // EF Core logs a failed command three times; the exception handler's one line with the stack is enough.
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LevelAlias.Off)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Query", LevelAlias.Off)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Update", LevelAlias.Off)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("AppVersion", info.Version)
            .Enrich.WithProperty("AppCommit", info.Commit)
            .Enrich.WithProperty("AppEnvironment", info.Environment)
            .Enrich.With<TraceEnricher>();

    /// <summary>
    /// Request context on every line logged during the request, then one Serilog summary line per request
    /// (method, path without the query string, route, status, duration). Health checks are not logged.
    /// </summary>
    public static IApplicationBuilder UseAppRequestLogging(this IApplicationBuilder app)
    {
        app.UseMiddleware<RequestLogContextMiddleware>();
        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"), branch => branch.UseSerilogRequestLogging(options =>
        {
            // The host's logger, not Serilog's static Log.Logger (left alone so test hosts don't share one).
            options.Logger = branch.ApplicationServices.GetRequiredService<Serilog.ILogger>();
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";
            options.IncludeQueryInRequestPath = false;
            options.EnrichDiagnosticContext = (diagnostics, context) =>
            {
                if (context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } route })
                {
                    diagnostics.Set("Route", route);
                }
            };
        }));
        return app;
    }

    /// <summary>The X-Journey header as a short safe token (letters, digits, '-', '_', '.'), or "none".</summary>
    internal static string Journey(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return "none";
        }

        var trimmed = header.Trim();
        return trimmed.Length <= MaxJourneyLength && trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? trimmed.ToLowerInvariant()
            : "invalid";
    }

    /// <summary>Every flag as "on", "off" or "on 20%" (flags that are on for a share of calls).</summary>
    internal static Dictionary<string, string> FlagValues(IFeatureFlags flags) =>
        Flags.All.ToDictionary(name => name, name =>
        {
            var flag = flags.GetFlag(name);
            if (!flag.Enabled)
            {
                return "off";
            }

            return FlagFeatureDefinitionProvider.Percent(name, flag) is { } percent
                ? string.Create(CultureInfo.InvariantCulture, $"on {percent}%")
                : "on";
        }, StringComparer.Ordinal);
}

/// <summary>Pushes RequestId, Journey and Flags for the rest of the request (see <see cref="LoggingSetup"/>).</summary>
internal sealed class RequestLogContextMiddleware(RequestDelegate next, IFeatureFlags flags)
{
    public async Task InvokeAsync(HttpContext context)
    {
        using (LogContext.PushProperty("RequestId", context.TraceIdentifier))
        using (LogContext.PushProperty("Journey", LoggingSetup.Journey(context.Request.Headers[LoggingSetup.JourneyHeader])))
        using (LogContext.PushProperty("Flags", LoggingSetup.FlagValues(flags), destructureObjects: true))
        {
            await next(context);
        }
    }
}

/// <summary>TraceId and SpanId as ordinary properties (compact JSON also has them as @tr/@sp) for Logs Insights queries.</summary>
internal sealed class TraceEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var traceId = logEvent.TraceId?.ToHexString() ?? Activity.Current?.TraceId.ToHexString();
        var spanId = logEvent.SpanId?.ToHexString() ?? Activity.Current?.SpanId.ToHexString();
        if (traceId is not null && traceId != default(ActivityTraceId).ToHexString())
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("TraceId", traceId));
            if (spanId is not null)
            {
                logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("SpanId", spanId));
            }
        }
    }
}
