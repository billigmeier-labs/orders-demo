using System.Globalization;

namespace Orders.LoadGenerator;

/// <summary>TARGET_BASE_URL, SCENARIO and DURATION_MINUTES, as the console's RunTask sets them.</summary>
internal sealed record LoadSettings(Uri TargetBaseUrl, LoadScenario Scenario, TimeSpan Duration)
{
    public const string DefaultTarget = "http://localhost:8080";

    public static LoadSettings From(Func<string, string?> env)
    {
        var target = env("TARGET_BASE_URL") is { Length: > 0 } t ? t : DefaultTarget;
        if (!Uri.TryCreate(target.TrimEnd('/') + "/", UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException($"TARGET_BASE_URL must be an http(s) URL, not '{target}'.");
        }

        var scenarioName = env("SCENARIO") is { Length: > 0 } s ? s : "normal";
        if (!Enum.TryParse<LoadScenario>(scenarioName, ignoreCase: true, out var scenario) || !Enum.IsDefined(scenario))
        {
            throw new ArgumentException($"SCENARIO must be one of {string.Join(", ", Enum.GetNames<LoadScenario>()).ToLowerInvariant()}, not '{scenarioName}'.");
        }

        var minutesText = env("DURATION_MINUTES");
        var minutes = scenario == LoadScenario.Soak ? 30d : 5d;
        if (minutesText is { Length: > 0 } && (!double.TryParse(minutesText, NumberStyles.Float, CultureInfo.InvariantCulture, out minutes) || minutes is <= 0 or > 24 * 60))
        {
            throw new ArgumentException($"DURATION_MINUTES must be a number of minutes between 0 and 1440, not '{minutesText}'.");
        }

        return new LoadSettings(url, scenario, TimeSpan.FromMinutes(minutes));
    }
}

public enum LoadScenario
{
    /// <summary>About 5 requests per second.</summary>
    Normal,

    /// <summary>About 25 requests per second.</summary>
    Peak,

    /// <summary>Normal, with a 60 s burst to about 60 requests per second in the middle.</summary>
    Spike,

    /// <summary>Normal for the whole (by default longer) duration.</summary>
    Soak,
}
