using System.Collections.Frozen;

namespace DecisionApps.ApiService.Services;

public static class ExcelScopeMapping
{
    // Baseline derived from the supplied cards and confirmed for Foglio origine.
    private static readonly FrozenDictionary<string, string> Agents = new Dictionary<string, string>
    {
        ["AMA"] = "informativo",
        ["ATAC"] = "informativo",
        ["INFORMATIVO PORTALE"] = "informativo",
        ["NEWS"] = "informativo",
        ["RSM"] = "informativo",
        ["CRM"] = "crm",
        ["ANAGRAFE"] = "anagrafe",
        ["SCUOLA"] = "scuola",
        ["TVA"] = "tva"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static string? Resolve(string? scope)
    {
        var normalized = Normalize(scope);
        if (normalized.StartsWith("AMBITO ", StringComparison.Ordinal))
        {
            normalized = normalized["AMBITO ".Length..];
        }
        return Agents.GetValueOrDefault(normalized);
    }

    public static string Normalize(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
