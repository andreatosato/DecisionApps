using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Pong.Web.Agents;
using Pong.Web.Decision;

namespace Pong.Tests;

[CollectionDefinition("Telemetry", DisableParallelization = true)]
public sealed class TelemetryCollection;

[Collection("Telemetry")]
public sealed class TelemetryTests
{
    [Fact]
    public async Task GptReportsCorrelatedModelTimingAndTokensWithItsOwnDeploymentTags()
    {
        using var capture = new Capture();
        using var factory = new TestHost { EnableGpt = true, UseApiKey = true };
        factory.Handler.ResponseBody = FakeHandler.ValidGptResponse;
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State() with { Model = "gpt" });
        response.EnsureSuccessStatusCode();
        var model = Assert.Single(capture.Spans, span => span.Source.Name == DecisionTelemetry.ModelSourceName);
        var agent = Assert.Single(capture.Spans, span =>
            span.GetTagItem("gen_ai.operation.name") as string == "invoke_agent");
        Assert.Equal(agent.SpanId, model.ParentSpanId);
        Assert.Equal("azure.ai.openai", model.GetTagItem("gen_ai.provider.name"));
        Assert.Equal("chat", model.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("gpt-6-luna", model.GetTagItem("gen_ai.request.model"));
        Assert.Equal("chat_completions", model.GetTagItem("decision.protocol"));
        Assert.Equal(22L, model.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(145d, Convert.ToDouble(agent.GetTagItem("gen_ai.usage.input_tokens")) +
            Convert.ToDouble(agent.GetTagItem("gen_ai.usage.output_tokens")));
        var duration = Assert.Single(capture.Measurements, m => m.Name == "gen_ai.client.operation.duration");
        Assert.Equal(model.Duration.TotalSeconds, duration.Value, 5);
        Assert.All(capture.Measurements, m => Assert.Equal("gpt-6-luna", m.Tags["gen_ai.request.model"]));
    }

    [Fact]
    public async Task CorrelatesAgentAndModelWithGenAiAttributesAndActualMetrics()
    {
        using var capture = new Capture();
        using var factory = new TestHost { UseApiKey = true };
        factory.Handler.ResponseBody = FakeHandler.ValidResponse.Replace(
            "\"answers\":", "\"model\":\"microsoft-decision-1.1\",\"answers\":");
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.ObservedState());
        response.EnsureSuccessStatusCode();

        var model = Assert.Single(capture.Spans, span => span.Source.Name == DecisionTelemetry.ModelSourceName);
        var agent = Assert.Single(capture.Spans, span =>
            span.GetTagItem("gen_ai.operation.name") as string == "invoke_agent");
        Assert.Equal(agent.TraceId, model.TraceId);
        Assert.Equal(agent.SpanId, model.ParentSpanId);
        Assert.Equal(ActivityKind.Client, model.Kind);
        Assert.Equal("classify Decision-1.1", model.DisplayName);
        Assert.Equal("classify", model.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("azure.ai.inference", model.GetTagItem("gen_ai.provider.name"));
        Assert.Equal("Decision-1.1", model.GetTagItem("gen_ai.request.model"));
        Assert.Equal("microsoft-decision-1.1", model.GetTagItem("gen_ai.response.model"));
        Assert.Equal("offline.invalid", model.GetTagItem("server.address"));
        Assert.Equal(443, model.GetTagItem("server.port"));
        Assert.Equal("systemone", model.GetTagItem("decision.protocol"));
        Assert.Equal(200, model.GetTagItem("http.response.status_code"));
        Assert.Equal("offline-request-id", model.GetTagItem("decision.request.id"));
        Assert.Equal("up", model.GetTagItem("decision.action"));
        Assert.Equal("up", agent.GetTagItem("decision.action"));
        Assert.Equal(123L, model.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(2L, model.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(123d, Convert.ToDouble(agent.GetTagItem("gen_ai.usage.input_tokens")));
        Assert.NotEqual(ActivityStatusCode.Error, model.Status);
        var duration = Assert.Single(capture.Measurements, m => m.Name == "gen_ai.client.operation.duration");
        Assert.Equal("s", duration.Unit);
        Assert.True(duration.Value >= 0);
        Assert.Equal(model.Duration.TotalSeconds, duration.Value, 5);
        Assert.Equal("microsoft-decision-1.1", duration.Tags["gen_ai.response.model"]);
        Assert.Equal(123, Assert.Single(capture.Measurements, m =>
            m.Name == "gen_ai.client.token.usage" && m.Tags["gen_ai.token.type"] as string == "input").Value);
        Assert.Equal(2, Assert.Single(capture.Measurements, m =>
            m.Name == "gen_ai.client.token.usage" && m.Tags["gen_ai.token.type"] as string == "output").Value);
        Assert.All(capture.Measurements, m =>
        {
            Assert.Equal("azure.ai.inference", m.Tags["gen_ai.provider.name"]);
            Assert.Equal("Decision-1.1", m.Tags["gen_ai.request.model"]);
            Assert.False(m.Tags.ContainsKey("decision.action"));
        });
        var telemetry = string.Join("\n", capture.Spans.SelectMany(span => span.TagObjects)
            .Select(tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain("offline-test-key", telemetry);
        Assert.DoesNotContain("Authorization", telemetry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("velocityX", telemetry);
        Assert.DoesNotContain("criteria", telemetry);
        Assert.DoesNotContain("\"history\":", telemetry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ballX", telemetry);
        Assert.DoesNotContain("paddleY", telemetry);
        Assert.DoesNotContain("timestampMilliseconds", telemetry);
        Assert.DoesNotContain("estimatedResponseMilliseconds", telemetry);
    }

    [Fact]
    public async Task ServiceDefaultsExportRegisteredGenAiSpansAndMetrics()
    {
        using var traces = new TraceExporter();
        using var metrics = new MetricExporter();
        using var factory = new TestHost
        {
            CustomizeServices = services => services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.SetSampler(new AlwaysOnSampler())
                    .AddProcessor(new SimpleActivityExportProcessor(traces)))
                .WithMetrics(meter => meter.AddReader(new PeriodicExportingMetricReader(
                    metrics, exportIntervalMilliseconds: int.MaxValue)))
        };
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State());
        response.EnsureSuccessStatusCode();
        Assert.True(factory.Services.GetRequiredService<TracerProvider>().ForceFlush());
        Assert.True(factory.Services.GetRequiredService<MeterProvider>().ForceFlush());
        var model = Assert.Single(traces.Spans, span => span.Source.Name == DecisionTelemetry.ModelSourceName);
        var agent = Assert.Single(traces.Spans, span => span.Source.Name == DecisionTelemetry.AgentSourceName);
        Assert.Equal(agent.SpanId, model.ParentSpanId);
        Assert.Contains("gen_ai.client.operation.duration", metrics.Names);
        Assert.Contains("gen_ai.client.token.usage", metrics.Names);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingUsageIsCountedButReportedZeroRemainsZero(bool missing)
    {
        using var capture = new Capture();
        using var factory = new TestHost();
        factory.Handler.ResponseBody = missing
            ? """{"answers":{"action":{"type":"choice","choice":"up","probabilities":{"up":1,"down":0,"stay":0}}}}"""
            : FakeHandler.ValidResponse.Replace("123", "0").Replace("\"output_tokens\":2", "\"output_tokens\":0")
                .Replace("125", "0");
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State());
        response.EnsureSuccessStatusCode();
        var tokens = capture.Measurements.Where(m => m.Name == "gen_ai.client.token.usage").ToArray();
        if (missing)
        {
            Assert.Empty(tokens);
            Assert.Equal(1, Assert.Single(capture.Measurements,
                m => m.Name == "decision.client.responses_without_usage").Value);
        }
        else
        {
            Assert.Equal(2, tokens.Length);
            Assert.All(tokens, token => Assert.Equal(0, token.Value));
            Assert.DoesNotContain(capture.Measurements, m => m.Name == "decision.client.responses_without_usage");
        }
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task HttpErrorsPreserveUpstreamCodeWithoutInventingUsage(int status)
    {
        using var capture = new Capture();
        using var factory = new TestHost();
        factory.Handler.StatusCode = (HttpStatusCode)status;
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State());
        Assert.False(response.IsSuccessStatusCode);
        AssertFailure(capture, status.ToString());
        Assert.Equal(status, Model(capture).GetTagItem("http.response.status_code"));
        Assert.Equal(1, factory.Handler.Calls);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("connection_error")]
    [InlineData("invalid_response")]
    public async Task RecordsTransportTimeoutAndInvalidPayloadFailures(string errorType)
    {
        using var capture = new Capture();
        using var factory = new TestHost();
        if (errorType == "timeout") factory.Handler.Failure = new TaskCanceledException();
        else if (errorType == "connection_error") factory.Handler.Failure = new HttpRequestException();
        else factory.Handler.ResponseBody = "{}";
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State());
        Assert.False(response.IsSuccessStatusCode);
        AssertFailure(capture, errorType);
    }

    [Fact]
    public async Task CancellationIsDistinguishedFromFailureAndDoesNotInventUsage()
    {
        using var capture = new Capture();
        using var factory = new TestHost();
        factory.Handler.WaitUntilCancelled = true;
        using var client = factory.CreateClient();
        var decision = factory.Services.GetRequiredService<DecisionClient>();
        using var cancellation = new CancellationTokenSource();
        var task = decision.EvaluateAsync(
            TestHost.State(), PongDecisionPrompt.Instructions, PongDecisionPrompt.Criteria, cancellation.Token);
        await factory.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(true, Model(capture).GetTagItem("decision.cancelled"));
        Assert.NotEqual(ActivityStatusCode.Error, Model(capture).Status);
        var duration = Assert.Single(capture.Measurements);
        Assert.Equal("gen_ai.client.operation.duration", duration.Name);
        Assert.Equal(true, duration.Tags["decision.cancelled"]);
        Assert.False(duration.Tags.ContainsKey("error.type"));
    }

    [Fact]
    public async Task AuthenticationFailureIsAttributedToAgentWithoutFictitiousInference()
    {
        using var capture = new Capture();
        using var factory = new TestHost();
        factory.Credential.Failure = new AuthenticationFailedException("private-credential-detail");
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var agent = Assert.Single(capture.Spans, s => s.Source.Name == DecisionTelemetry.AgentSourceName);
        Assert.Equal("authentication_error", agent.GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Error, agent.Status);
        Assert.DoesNotContain(capture.Spans, s => s.Source.Name == DecisionTelemetry.ModelSourceName);
        Assert.Empty(capture.Measurements);
        Assert.Equal(0, factory.Handler.Calls);
    }

    private static Activity Model(Capture capture) =>
        Assert.Single(capture.Spans, s => s.Source.Name == DecisionTelemetry.ModelSourceName);

    private static void AssertFailure(Capture capture, string errorType)
    {
        Assert.Equal(ActivityStatusCode.Error, Model(capture).Status);
        Assert.Equal(errorType, Model(capture).GetTagItem("error.type"));
        Assert.Null(Model(capture).GetTagItem("gen_ai.usage.input_tokens"));
        var duration = Assert.Single(capture.Measurements);
        Assert.Equal("gen_ai.client.operation.duration", duration.Name);
        Assert.Equal(errorType, duration.Tags["error.type"]);
    }

    private sealed record Measurement(string Name, string? Unit, double Value, Dictionary<string, object?> Tags);

    private sealed class TraceExporter : BaseExporter<Activity>
    {
        public ConcurrentBag<Activity> Spans { get; } = [];

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var span in batch) Spans.Add(span);
            return ExportResult.Success;
        }
    }

    private sealed class MetricExporter : BaseExporter<Metric>
    {
        public ConcurrentBag<string> Names { get; } = [];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (metric.MeterName == DecisionTelemetry.ModelSourceName) Names.Add(metric.Name);
            }
            return ExportResult.Success;
        }
    }

    private sealed class Capture : IDisposable
    {
        public ConcurrentBag<Activity> Spans { get; } = [];
        public ConcurrentBag<Measurement> Measurements { get; } = [];
        private readonly ActivityListener activities;
        private readonly MeterListener meters;

        public Capture()
        {
            activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name is DecisionTelemetry.AgentSourceName or DecisionTelemetry.ModelSourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = Spans.Add
            };
            ActivitySource.AddActivityListener(activities);
            meters = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == DecisionTelemetry.ModelSourceName) listener.EnableMeasurementEvents(instrument);
                }
            };
            meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            meters.Start();
        }

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
            Measurements.Add(new(instrument.Name, instrument.Unit, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));

        public void Dispose()
        {
            activities.Dispose();
            meters.Dispose();
        }
    }
}
