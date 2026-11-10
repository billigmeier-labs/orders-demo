using System.Text.RegularExpressions;

namespace Orders.Console.Integrations.GitHub;

/// <summary>
/// Enough of GitHub's CODEOWNERS matching for the console's "does this PR need the code owner" check: one pattern per
/// line (the owners are ignored), <c>#</c> comments, a leading <c>/</c> anchoring to the repo root, a trailing <c>/</c>
/// meaning everything under that directory, and <c>*</c> / <c>**</c> wildcards.
/// </summary>
public static class CodeOwners
{
    public static IReadOnlyList<Regex> Parse(string text) =>
        text.Split('\n')
            .Select(line => line.Split('#')[0].Trim())
            .Where(line => line.Length > 0)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0])
            .Select(ToRegex)
            .ToList();

    public static bool Matches(IReadOnlyList<Regex> patterns, string path) => patterns.Any(p => p.IsMatch(path));

    private static Regex ToRegex(string pattern)
    {
        var anchored = pattern.StartsWith('/');
        var body = pattern.TrimStart('/');
        var directory = body.EndsWith('/');
        body = body.TrimEnd('/');

        var regex = Regex.Escape(body)
            .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal);
        var prefix = anchored ? "^" : "^(?:.*/)?";
        var suffix = directory ? "/.*$" : "(?:/.*)?$";
        return new Regex(prefix + regex + suffix, RegexOptions.CultureInvariant);
    }
}
