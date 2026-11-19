using System.Diagnostics;
using System.Diagnostics.Metrics;
using DecisionApps.Contracts;

namespace DecisionApps.ApiService.Services;

public static class DecisionTelemetry
{
    public const string AgentSourceName = "DecisionApps.Agents";
    public const string ModelSourceName = "DecisionApps.Models";
    public const string ProviderName = "azure.ai.inference";
    public const string OperationName = "classify";

    public static readonly ActivitySource ModelSource = new(ModelSourceName);
    private static readonly Meter Meter = new(ModelSourceName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "gen_ai.client.operation.duration", "s", "Decision classification duration.",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.01, 0.02, 0.04, 0.08, 0.16, 0.32, 0.64, 1.28, 2.56, 5.12, 10.24, 20.48, 40.96, 81.92]
        });
    private static readonly Histogram<long> Tokens = Meter.CreateHistogram<long>(
        "gen_ai.client.token.usage", "{token}", "Tokens reported by Decision per classification.",
        advice: new InstrumentAdvice<long>
        {
            HistogramBucketBoundaries = [1, 4, 16, 64, 256, 1024, 4096, 16384, 65536, 262144]
        });
    private static readonly Counter<long> MissingUsage = Meter.CreateCounter<long>(
        "decision.client.responses_without_usage", "{response}", "Decision responses without token usage.");

    public static TagList CreateTags(DecisionOptions options)
    {
        var endpoint = new Uri(options.Endpoint);
        return new TagList
        {
            { "gen_ai.operation.name", OperationName },
            { "gen_ai.provider.name", ProviderName },
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

        var inputTags = tags;
        inputTags.Add("gen_ai.token.type", "input");
        Tokens.Record(usage.InputTokens, inputTags);
        var outputTags = tags;
        outputTags.Add("gen_ai.token.type", "output");
        Tokens.Record(usage.OutputTokens, outputTags);
    }

    public static void RecordDuration(double seconds, in TagList tags) => Duration.Record(seconds, tags);
}

public sealed class GenAiTelemetryOptions
{
    public bool EnableSensitiveData { get; set; }
}
