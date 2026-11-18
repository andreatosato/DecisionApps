using System.Text;
using System.Text.Json;
using DecisionApps.Contracts;

namespace DecisionApps.ApiService.Services;

public sealed class AgentCatalog
{
    public IReadOnlyList<AgentCard> Agents { get; }

    public AgentCatalog(IHostEnvironment environment)
    {
        string[] ids = ["informativo", "crm", "scuola", "anagrafe", "tva"];
        Agents = ids.Select(id =>
        {
            var path = Path.Combine(environment.ContentRootPath, "AgentCards", $"{id}.json");
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            using var card = JsonDocument.Parse(reader.ReadToEnd());
            return new AgentCard(id, card.RootElement.GetProperty("name").GetString()!,
                card.RootElement.GetProperty("description").GetString()!);
        }).ToArray();
    }
}
