namespace Orders.Console.Integrations;

/// <summary>
/// Descriptions for the API's feature flags (docs/contracts.md), used when AppConfig does not return one.
/// AppConfig keeps a flag's description in <c>_description</c> and its display name in <c>name</c>, which for
/// these flags is just the key again.
/// </summary>
public static class KnownFlags
{
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["searchV2"] = "New search engine (has a hidden multi-word bug for the incident demo)",
        ["faultFailOrders"] = "Fault: make some order requests fail",
        ["faultSlowSearch"] = "Fault: add 2 s to search",
    };

    public static string? Describe(string name) => Descriptions.GetValueOrDefault(name);
}
