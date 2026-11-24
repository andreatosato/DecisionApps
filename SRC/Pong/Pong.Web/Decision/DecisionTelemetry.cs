using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Pong.Web.Contracts;

namespace Pong.Web.Decision;

public static class DecisionTelemetry
{
    public const string AgentSourceName = "Pong.Agents";
    public const string ModelSourceName = "Pong.Models";
    public const string ProviderName = "azure.ai.inference";
    public const string OperationName = "classify";

    public static readonly ActivitySource ModelSource = new(ModelSourceName);
    private static readonly Meter Meter = new(ModelSourceName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "gen_ai.client.operation.duration", "s", "Duration of the buffered model HTTP request.",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.01, 0.02, 0.04, 0.08, 0.16, 0.32, 0.64, 1.28, 2.56, 5.12, 10.24]
        });
    private static readonly Histogram<long> Tokens = Meter.CreateHistogram<long>(
        "gen_ai.client.token.usage", "{token}", "Token counts reported by the selected model.",
        advice: new InstrumentAdvice<long>
        {
            HistogramBucketBoundaries = [1, 4, 16, 64, 256, 1024, 4096, 16384, 65536]
        });
    private static readonly Counter<long> MissingUsage = Meter.CreateCounter<long>(
        "decision.client.responses_without_usage", "{response}", "Valid model responses without token usage.");

    public static TagList CreateTags(
        DecisionOptions options, string provider = ProviderName, string operation = OperationName)
    {
        var endpoint = new Uri(options.Endpoint);
        return new TagList
        {
            { "gen_ai.operation.name", operation },
            { "gen_ai.provider.name", provider },
            { "gen_ai.request.model", options.Deployment },
            { "server.address", endpoint.Host },
            { "server.port", endpoint.Port }
        };
    }

    public static void RecordUsage(TokenUsage? usage, in TagList tags)
    {
        if (usage is null)
        {
            MissingUsage.Add(1, tags);
            return;
        }
        var input = tags;
        input.Add("gen_ai.token.type", "input");
        Tokens.Record(usage.InputTokens, input);
        var output = tags;
        output.Add("gen_ai.token.type", "output");
        Tokens.Record(usage.OutputTokens, output);
    }

    public static void RecordDuration(double seconds, in TagList tags) => Duration.Record(seconds, tags);

    public static void RecordMessages(Activity activity, string attributeName, string role, string content) =>
        activity.SetTag(attributeName, JsonSerializer.Serialize(new[]
        {
            new { role, parts = new[] { new { type = "text", content } } }
        }));

    public static void RecordCancellation(Activity? activity, string phase)
    {
        activity?.SetTag("decision.cancelled", true);
        activity?.SetTag("decision.cancellation.phase", phase);
    }
}

public sealed class GenAiTelemetryOptions
{
    public bool EnableSensitiveData { get; set; }
}
