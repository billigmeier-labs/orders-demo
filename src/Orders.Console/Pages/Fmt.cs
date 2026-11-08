using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Orders.Console.Scenarios;

namespace Orders.Console.Pages;

/// <summary>Display helpers. Times are 12-hour; the page script rewrites them into the viewer's time zone.</summary>
public static class Fmt
{
    public static IHtmlContent Time(DateTimeOffset? at)
    {
        if (at is null || at == DateTimeOffset.MinValue)
        {
            return new HtmlString("");
        }

        var utc = at.Value.ToUniversalTime();
        var iso = utc.ToString("o", CultureInfo.InvariantCulture);
        var text = utc.ToString("h:mm tt", CultureInfo.InvariantCulture) + " UTC";
        return new HtmlString($"<time datetime=\"{HtmlEncoder.Default.Encode(iso)}\">{HtmlEncoder.Default.Encode(text)}</time>");
    }

    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var d = now - at;
        return d.TotalSeconds switch
        {
            < 0 => "just now",
            < 60 => $"{(int)d.TotalSeconds} s ago",
            < 3600 => $"{(int)d.TotalMinutes} min ago",
            < 86400 => $"{(int)d.TotalHours} h ago",
            _ => $"{(int)d.TotalDays} d ago",
        };
    }

    public static string Seconds(TimeSpan t) => $"{Math.Max(0, (int)t.TotalSeconds)} s";

    public static string StateLabel(string state) => state switch
    {
        StepStates.Ready => "Ready",
        StepStates.Running => "Working",
        StepStates.Waiting => "Waiting",
        StepStates.Done => "Done",
        StepStates.Failed => "Failed",
        StepStates.TimedOut => "Timed out",
        StepStates.Locked => "Locked",
        _ => "Not yet",
    };
}
