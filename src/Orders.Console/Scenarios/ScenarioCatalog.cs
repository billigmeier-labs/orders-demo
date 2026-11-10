using System.Text.Json;
using Orders.Console.Domain;

namespace Orders.Console.Scenarios;

/// <summary>Loads and validates every scenario JSON file. Invalid files stop the app at startup.</summary>
public sealed class ScenarioCatalog
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, ScenarioDefinition> _byId;

    public ScenarioCatalog(IEnumerable<ScenarioDefinition> scenarios, IReadOnlySet<string> actionKinds, IReadOnlySet<string> conditionNames)
    {
        var list = scenarios.OrderBy(s => s.Order).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        var errors = Validate(list, actionKinds, conditionNames);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid scenario definitions:\n" + string.Join("\n", errors));
        }

        All = list;
        _byId = list.ToDictionary(s => s.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<ScenarioDefinition> All { get; }

    public ScenarioDefinition Tour => All.First(s => s.Tour);

    public IEnumerable<ScenarioDefinition> Library => All.Where(s => !s.Tour);

    public ScenarioDefinition? Find(string id) => _byId.GetValueOrDefault(id);

    public static IReadOnlyList<ScenarioDefinition> LoadDirectory(string directory)
    {
        var result = new List<ScenarioDefinition>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var def = JsonSerializer.Deserialize<ScenarioDefinition>(File.ReadAllText(file), Json)
                    ?? throw new InvalidOperationException("File is empty.");
                result.Add(def);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{Path.GetFileName(file)}: {ex.Message}", ex);
            }
        }

        return result;
    }

    public static List<string> Validate(IReadOnlyList<ScenarioDefinition> list, IReadOnlySet<string> actionKinds, IReadOnlySet<string> conditionNames)
    {
        var errors = new List<string>();
        foreach (var dup in list.GroupBy(s => s.Id).Where(g => g.Count() > 1))
        {
            errors.Add($"Scenario id '{dup.Key}' is used more than once.");
        }

        if (list.Count(s => s.Tour) != 1)
        {
            errors.Add("Exactly one scenario must have \"tour\": true.");
        }

        foreach (var s in list)
        {
            if (s.Steps.Count == 0)
            {
                errors.Add($"{s.Id}: has no steps.");
            }

            foreach (var dup in s.Steps.GroupBy(x => x.Id).Where(g => g.Count() > 1))
            {
                errors.Add($"{s.Id}: step id '{dup.Key}' is used more than once.");
            }

            foreach (var a in s.Steps.Select(x => x.Action).OfType<ActionDefinition>().Concat(s.Reset))
            {
                if (!actionKinds.Contains(a.Kind))
                {
                    errors.Add($"{s.Id}: unknown action kind '{a.Kind}'.");
                }
            }

            foreach (var step in s.Steps)
            {
                if (step.TimeoutSeconds <= 0)
                {
                    errors.Add($"{s.Id}/{step.Id}: timeoutSeconds must be positive.");
                }

                foreach (var w in step.Wait.Concat(step.FailOn))
                {
                    if ((w.Kind is null) == (w.Condition is null))
                    {
                        errors.Add($"{s.Id}/{step.Id}: '{w.Description}' needs exactly one of kind or condition.");
                    }

                    if (w.Kind is not null && !EventKinds.All.Contains(w.Kind))
                    {
                        errors.Add($"{s.Id}/{step.Id}: unknown event kind '{w.Kind}'.");
                    }

                    if (w.Condition is not null && !conditionNames.Contains(w.Condition))
                    {
                        errors.Add($"{s.Id}/{step.Id}: unknown condition '{w.Condition}'.");
                    }

                    if (w.SinceRunStart && !Matching.IsCorrelated(w))
                    {
                        errors.Add($"{s.Id}/{step.Id}: '{w.Description}' has sinceRunStart but is not about the run's issue or PR.");
                    }
                }
            }
        }

        return errors;
    }
}
