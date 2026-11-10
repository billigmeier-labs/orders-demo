using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Orders.Console.Integrations;
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

    public static string Rate(double perMinute) => perMinute.ToString(perMinute < 10 ? "0.#" : "N0", CultureInfo.InvariantCulture) + "/min";

    public static string Percent(double share) => (share * 100).ToString(share is > 0 and < 0.1 ? "0.0#" : "0.#", CultureInfo.InvariantCulture) + "%";

    public static string Ms(TimeSpan? t) => t is { } v
        ? v.TotalSeconds >= 1 ? v.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s" : ((int)v.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms"
        : "n/a";

    /// <summary>One bar per minute, its 5xx share drawn on top in the alarm colour.</summary>
    public static IHtmlContent Sparkline(IReadOnlyList<TrafficPoint> minutes, string label)
    {
        const int width = 150, height = 32, gap = 2;
        var max = Math.Max(1, minutes.Count == 0 ? 1 : minutes.Max(m => m.Requests));
        var bar = minutes.Count == 0 ? width : (double)(width - (gap * (minutes.Count - 1))) / minutes.Count;
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg class=\"spark\" viewBox=\"0 0 {width} {height}\" preserveAspectRatio=\"none\" role=\"img\" aria-label=\"{HtmlEncoder.Default.Encode(label)}\">");
        for (var i = 0; i < minutes.Count; i++)
        {
            var m = minutes[i];
            var x = i * (bar + gap);
            var h = Math.Max(m.Requests > 0 ? 1 : 0, height * m.Requests / max);
            var e = m.Requests > 0 ? h * Math.Min(1, m.Errors5xx / m.Requests) : 0;
            svg.Append(CultureInfo.InvariantCulture, $"<rect class=\"spark-bar\" x=\"{x:0.##}\" y=\"{height - h:0.##}\" width=\"{bar:0.##}\" height=\"{h:0.##}\"></rect>");
            if (e > 0)
            {
                svg.Append(CultureInfo.InvariantCulture, $"<rect class=\"spark-err\" x=\"{x:0.##}\" y=\"{height - h:0.##}\" width=\"{bar:0.##}\" height=\"{Math.Max(1, e):0.##}\"></rect>");
            }
        }

        svg.Append("</svg>");
        return new HtmlString(svg.ToString());
    }

    /// <summary>A scoreboard target's verdict: met, missed, or no data yet.</summary>
    public static (string Text, string Css) Verdict(bool? pass) => pass switch
    {
        true => ("met", "pill-ok"),
        false => ("missed", "pill-bad"),
        null => ("no data", "pill-muted"),
    };

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
