using System.Text.Json;
using Pong.Web.Contracts;

namespace Pong.Web.Decision;

public static class DecisionResponseMapper
{
    public static DecisionResult Map(JsonElement payload, long sequence, long elapsed, string deployment)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object ||
            !answers.TryGetProperty("action", out var answer) || answer.ValueKind != JsonValueKind.Object ||
            !answer.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "choice" ||
            !answer.TryGetProperty("choice", out var choice) || choice.ValueKind != JsonValueKind.String ||
            choice.GetString() is not ("up" or "down" or "stay") ||
            !answer.TryGetProperty("probabilities", out var probabilities) || probabilities.ValueKind != JsonValueKind.Object ||
            probabilities.EnumerateObject().Count() != 3 ||
            !probabilities.TryGetProperty("up", out var upElement) || !TryProbability(upElement, out var up) ||
            !probabilities.TryGetProperty("down", out var downElement) || !TryProbability(downElement, out var down) ||
            !probabilities.TryGetProperty("stay", out var stayElement) || !TryProbability(stayElement, out var stay))
        {
            throw new DecisionException("Decision returned an invalid action or probabilities.");
        }
        var sum = up + down + stay;
        var selectedProbability = choice.GetString() switch { "up" => up, "down" => down, _ => stay };
        if (Math.Abs(sum - 1m) > 0.001m ||
            selectedProbability < Math.Max(up, Math.Max(down, stay)))
        {
            throw new DecisionException("Decision probabilities are not normalized or contradict the selected action.");
        }

        TokenUsage? usage = null;
        if (payload.TryGetProperty("usage", out var rawUsage) && rawUsage.ValueKind != JsonValueKind.Null)
        {
            if (rawUsage.ValueKind != JsonValueKind.Object ||
                !rawUsage.TryGetProperty("input_tokens", out var inputElement) || !TryTokenCount(inputElement, out var input) ||
                !rawUsage.TryGetProperty("output_tokens", out var outputElement) || !TryTokenCount(outputElement, out var output) ||
                input > long.MaxValue - output)
            {
                throw new DecisionException("Decision returned invalid token usage.");
            }
            if (rawUsage.TryGetProperty("total_tokens", out var totalElement) &&
                (!TryTokenCount(totalElement, out var total) || total != input + output))
            {
                throw new DecisionException("Decision returned inconsistent total token usage.");
            }
            usage = new TokenUsage(input, output, input + output);
        }
        return new DecisionResult(sequence, choice.GetString()!, new(up / sum, down / sum, stay / sum), elapsed, deployment, usage);
    }

    private static bool TryProbability(JsonElement element, out decimal value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value) && value is >= 0 and <= 1;
    }

    private static bool TryTokenCount(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value) && value >= 0;
    }
}
