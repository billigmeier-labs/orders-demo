using System.Text.RegularExpressions;
using Orders.Console.Domain;

namespace Orders.Console.Scenarios;

public static partial class Matching
{
    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"#(\d+)")]
    private static partial Regex Number();

    /// <summary>Fills {var} placeholders. Throws <see cref="MissingVariableException"/> for unknown ones.</summary>
    public static string Fill(string template, IReadOnlyDictionary<string, string> vars) =>
        Placeholder().Replace(template, m => vars.TryGetValue(m.Groups[1].Value, out var v)
            ? v
            : throw new MissingVariableException(m.Groups[1].Value));

    public static bool TryFill(string template, IReadOnlyDictionary<string, string> vars, out string filled)
    {
        try
        {
            filled = Fill(template, vars);
            return true;
        }
        catch (MissingVariableException)
        {
            filled = "";
            return false;
        }
    }

    /// <summary>True when the condition names a run variable, so it can only match this run's issue or PR.</summary>
    public static bool IsCorrelated(WaitCondition c) =>
        (c.Subject is not null && Placeholder().IsMatch(c.Subject)) || c.DetailContains.Any(d => Placeholder().IsMatch(d) && !d.Contains("{agent}", StringComparison.Ordinal));

    public static bool Matches(WaitCondition c, ConsoleEvent e, IReadOnlyDictionary<string, string> vars)
    {
        if (c.Kind is null || !string.Equals(c.Kind, e.Kind, StringComparison.Ordinal))
        {
            return false;
        }

        if (c.Subject is not null)
        {
            if (!TryFill(c.Subject, vars, out var subject) || !string.Equals(subject, e.Subject, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        foreach (var part in c.DetailContains)
        {
            if (!TryFill(part, vars, out var needle) || !ContainsToken(e.Detail ?? "", needle))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Case-insensitive contains where "#4" does not match inside "#42".</summary>
    public static bool ContainsToken(string haystack, string needle)
    {
        if (needle.Length == 0)
        {
            return true;
        }

        var start = 0;
        while (true)
        {
            var i = haystack.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
            if (i < 0)
            {
                return false;
            }

            var end = i + needle.Length;
            var boundaryOk = !char.IsLetterOrDigit(needle[^1]) || end >= haystack.Length || !char.IsLetterOrDigit(haystack[end]);
            if (boundaryOk)
            {
                return true;
            }

            start = i + 1;
        }
    }

    /// <summary>42 from "issue#42" or "pr#42".</summary>
    public static string? NumberIn(string subject)
    {
        var m = Number().Match(subject);
        return m.Success ? m.Groups[1].Value : null;
    }
}
