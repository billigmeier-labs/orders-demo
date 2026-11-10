using System.Text.Json;

namespace Orders.Console.Status;

/// <summary>
/// Turns one raw log line (the API's Serilog compact JSON, see Orders.Api Infrastructure/Logging.cs, or the older
/// Microsoft JSON console format) into a short
/// summary that fits on one line of a projector: request, exception type and the first line of its message,
/// e.g. <c>/api/search — PostgresException 42601: syntax error in tsquery</c>. Lines that are not JSON, or
/// lack those fields, fall back to the Message field and then to the raw line, truncated.
/// </summary>
public static class LogSummary
{
    public const int MaxLength = 200;

    public static string Summarize(string? raw)
    {
        var line = raw?.Trim() ?? "";
        if (line.Length == 0)
        {
            return "(empty log line)";
        }

        if (line[0] != '{')
        {
            return Truncate(FirstLine(line));
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Truncate(FirstLine(line));
            }

            var path = Find(root, "RequestPath") ?? Find(root, "Path");
            var method = Find(root, "RequestMethod") ?? Find(root, "Method");
            var request = path is null ? null : method is null ? path : $"{method} {path}";

            var exception = String(root, "@x") ?? String(root, "Exception");
            var message = String(root, "@m") ?? String(root, "Message") ?? String(root, "@mt");
            var problem = Exception(exception) ?? Nonempty(FirstLine(message ?? ""));
            var summary = (request, problem) switch
            {
                (not null, not null) => $"{request} — {problem}",
                (null, not null) => problem,
                (not null, null) => request,
                _ => FirstLine(line),
            };
            return Truncate(summary);
        }
        catch (JsonException)
        {
            return Truncate(FirstLine(line));
        }
    }

    /// <summary>"Npgsql.PostgresException (0x80004005): 42601: syntax error..." → "PostgresException 42601: syntax error...".</summary>
    internal static string? Exception(string? exception)
    {
        var first = Nonempty(FirstLine(exception ?? ""));
        if (first is null)
        {
            return null;
        }

        var colon = first.IndexOf(": ", StringComparison.Ordinal);
        var head = colon < 0 ? first : first[..colon];
        var message = colon < 0 ? null : Nonempty(first[(colon + 2)..]);

        var paren = head.IndexOf(" (", StringComparison.Ordinal);
        var type = (paren < 0 ? head : head[..paren]).Trim();
        if (type.Contains(' ', StringComparison.Ordinal))
        {
            // Not "Type: message"; show the line as it is.
            return first;
        }

        type = type[(type.LastIndexOf('.') + 1)..];
        return message is null ? type : $"{type} {message}";
    }

    /// <summary>A string property, at the top level, in State, or in any object in Scopes (case-insensitive).</summary>
    private static string? Find(JsonElement root, string name)
    {
        if (Nonempty(String(root, name)) is { } top)
        {
            return top;
        }

        if (Property(root, "State") is { ValueKind: JsonValueKind.Object } state && Nonempty(String(state, name)) is { } fromState)
        {
            return fromState;
        }

        if (Property(root, "Scopes") is { ValueKind: JsonValueKind.Array } scopes)
        {
            foreach (var scope in scopes.EnumerateArray())
            {
                if (scope.ValueKind == JsonValueKind.Object && Nonempty(String(scope, name)) is { } fromScope)
                {
                    return fromScope;
                }
            }
        }

        return null;
    }

    private static JsonElement? Property(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }

        return null;
    }

    private static string? String(JsonElement obj, string name) =>
        Property(obj, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static string? Nonempty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string FirstLine(string s)
    {
        var end = s.IndexOfAny(['\r', '\n']);
        return (end < 0 ? s : s[..end]).Trim();
    }

    private static string Truncate(string s) => s.Length <= MaxLength ? s : s[..(MaxLength - 1)].TrimEnd() + "…";
}
