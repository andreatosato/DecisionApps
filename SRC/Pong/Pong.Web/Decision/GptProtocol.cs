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
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = instructions + "\nAction criteria: " + JsonSerializer.Serialize(criteria) +
                        "\nReturn the chosen action and estimated probabilities summing to 1. The chosen action must have the highest probability."
                },
                new { role = "user", content = JsonSerializer.Serialize(state, JsonOptions) }
            },
            max_completion_tokens = 2048,
            store = false,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "pong_action",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            type = new { type = "string", @enum = new[] { "choice" } },
                            choice = new { type = "string", @enum = new[] { "up", "down", "stay" } },
                            probabilities = new
                            {
                                type = "object",
                                properties = new
                                {
                                    up = new { type = "number" },
                                    down = new { type = "number" },
                                    stay = new { type = "number" }
                                },
                                required = new[] { "up", "down", "stay" },
                                additionalProperties = false
                            }
                        },
                        required = new[] { "type", "choice", "probabilities" },
                        additionalProperties = false
                    }
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
        var choices = payload.GetProperty("choices");
        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
        {
            throw new DecisionException("GPT must return exactly one decision.");
        }
        var choice = choices[0];
        if (choice.GetProperty("finish_reason").GetString() != "stop")
        {
            throw new DecisionException("GPT returned an incomplete or filtered decision.");
        }
        var message = choice.GetProperty("message");
        if (message.GetProperty("role").GetString() != "assistant" ||
            (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null))
        {
            throw new DecisionException("GPT refused the decision or returned an unexpected message.");
        }
        var text = message.GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DecisionException("GPT returned no decision.");
        }
        using var content = JsonDocument.Parse(text);
        return DecisionResponseMapper.MapAnswer(
            content.RootElement, payload, sequence, elapsed, deployment, "prompt_tokens", "completion_tokens");
    }
}
