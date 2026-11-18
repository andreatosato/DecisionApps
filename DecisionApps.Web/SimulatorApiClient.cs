using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DecisionApps.Contracts;

namespace DecisionApps.Web;

public sealed class SimulatorApiClient(HttpClient http)
{
    public async Task<AgentCard[]> GetAgentsAsync(CancellationToken cancellationToken) =>
        await ReadAsync<AgentCard[]>(await http.GetAsync("/api/agents", cancellationToken), cancellationToken);

    public async Task<ModelConfiguration> GetConfigurationAsync(CancellationToken cancellationToken) =>
        await ReadAsync<ModelConfiguration>(await http.GetAsync("/api/configuration", cancellationToken), cancellationToken);

    public async Task<ImportResult> ImportAsync(Stream stream, string fileName, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(content, "file", fileName);
        return await ReadAsync<ImportResult>(await http.PostAsync("/api/import", form, cancellationToken), cancellationToken);
    }

    public async Task<RoutingResult> RateAsync(string question, CancellationToken cancellationToken) =>
        await ReadAsync<RoutingResult>(
            await http.PostAsJsonAsync("/api/ratings", new RatingRequest(question), cancellationToken), cancellationToken);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                var detail = $"API non disponibile (HTTP {(int)response.StatusCode}).";
                long? modelElapsedMilliseconds = null;
                if (response.Content.Headers.ContentType?.MediaType == "application/problem+json")
                {
                    using var problem = JsonDocument.Parse(text);
                    if (problem.RootElement.TryGetProperty("detail", out var message))
                    {
                        detail = message.GetString() ?? detail;
                    }
                    if (problem.RootElement.TryGetProperty("modelElapsedMilliseconds", out var elapsed))
                    {
                        if (elapsed.ValueKind != JsonValueKind.Number || !elapsed.TryGetInt64(out var duration) || duration < 0)
                        {
                            throw new JsonException("Durata della chiamata modello non valida nella risposta API.");
                        }
                        modelElapsedMilliseconds = duration;
                    }
                }
                throw new SimulatorApiException(detail, (int)response.StatusCode, modelElapsedMilliseconds);
            }
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                ?? throw new SimulatorApiException("L'API ha restituito una risposta vuota.", 502);
        }
    }
}

public sealed class SimulatorApiException(string message, int statusCode, long? modelElapsedMilliseconds = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public long? ModelElapsedMilliseconds { get; } = modelElapsedMilliseconds;
}
