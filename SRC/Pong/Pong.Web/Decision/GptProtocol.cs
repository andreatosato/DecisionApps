using System.Text.Json;
using Pong.Web.Contracts;

namespace Pong.Web.Decision;

internal static class GptProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static HttpContent CreateContent(
        string deployment, DecisionRequest state, string instructions, IReadOnlyDictionary<string, string> criteria)
    {
        return JsonContent.Create(new
        {
            model = deployment,
            input = JsonSerializer.Serialize(state, JsonOptions),
            questions = new[]
            {
                new
                {
                    type = "choice",
                    name = "action",
                    instructions,
                    choices = criteria.Select(criterion => new
                    {
                        value = criterion.Key,
                        description = criterion.Value
                    }).ToArray()
                }
            }
        });
    }

    public static DecisionResult Map(JsonElement payload, long sequence, long elapsed, string deployment)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            (payload.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null))
        {
            throw new DecisionException("GPT returned an error instead of a decision.");
        }
        var answers = payload.GetProperty("answers");
        if (answers.ValueKind != JsonValueKind.Array || answers.GetArrayLength() != 1)
        {
            throw new DecisionException("GPT must return exactly one decision.");
        }
        var answer = answers[0];
        if (answer.ValueKind != JsonValueKind.Object ||
            answer.GetProperty("name").GetString() != "action" ||
            answer.GetProperty("type").GetString() != "choice")
        {
            throw new DecisionException("GPT refused the decision or returned an unexpected answer.");
        }
        var rawProbabilities = answer.GetProperty("probabilities");
        if (rawProbabilities.ValueKind != JsonValueKind.Array || rawProbabilities.GetArrayLength() != 3)
        {
            throw new DecisionException("GPT returned invalid action probabilities.");
        }
        var probabilities = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var probability in rawProbabilities.EnumerateArray())
        {
            if (probability.ValueKind != JsonValueKind.Object ||
                probability.GetProperty("value").GetString() is not { } value ||
                !probabilities.TryAdd(value, probability.GetProperty("probability")))
            {
                throw new DecisionException("GPT returned invalid or duplicate action probabilities.");
            }
        }
        var normalized = JsonSerializer.SerializeToElement(new
        {
            type = "choice",
            choice = answer.GetProperty("choice"),
            probabilities
        });
        return DecisionResponseMapper.MapAnswer(
            normalized, payload, sequence, elapsed, deployment);
    }
}
