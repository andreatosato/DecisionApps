using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using DecisionApps.ApiService.Services;
using DecisionApps.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace DecisionApps.Tests;

[CollectionDefinition("Telemetry", DisableParallelization = true)]
public sealed class TelemetryCollection;

[Collection("Telemetry")]
public sealed class TelemetryTests
{
    [Fact]
    public async Task ExportsCorrelatedAgentAndModelSpansAndActualUsageWithoutPrivateContent()
    {
        using var capture = new Capture();
        using var client = capture.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest("PRIVATE-QUESTION-123"));
        response.EnsureSuccessStatusCode();

        var agent = Assert.Single(capture.Spans, span => span.GetTagItem("gen_ai.operation.name") as string == "invoke_agent");
        var model = Assert.Single(capture.Spans, span => span.Source.Name == DecisionTelemetry.ModelSourceName);
        Assert.Equal(agent.SpanId, model.ParentSpanId);
        Assert.Equal(ActivityKind.Client, model.Kind);
        Assert.Equal("classify Decision-1.1", model.DisplayName);
        Assert.Equal("classify", model.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("azure.ai.inference", model.GetTagItem("gen_ai.provider.name"));
        Assert.Equal("Decision-1.1", model.GetTagItem("gen_ai.request.model"));
        Assert.Equal("systemone", model.GetTagItem("decision.protocol"));
        Assert.Equal("agenteinformativo.services.ai.azure.com", model.GetTagItem("server.address"));
        Assert.Equal(443, model.GetTagItem("server.port"));
        Assert.Equal("test-model-request-id", model.GetTagItem("decision.request.id"));
        Assert.Equal(200, model.GetTagItem("http.response.status_code"));
        Assert.Equal(1234L, model.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(2L, model.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(1234d, Convert.ToDouble(agent.GetTagItem("gen_ai.usage.input_tokens")));
        Assert.Equal("crm", agent.GetTagItem("decision.agent"));
        Assert.NotNull(model.GetTagItem("gen_ai.response.model"));
        Assert.NotEqual(ActivityStatusCode.Error, model.Status);
        Assert.True(model.Duration > TimeSpan.Zero);
        var telemetry = capture.SerializedSpans();
        Assert.DoesNotContain("PRIVATE-QUESTION-123", telemetry);
        Assert.DoesNotContain("PRIVATE-API-KEY-123", telemetry);
        Assert.DoesNotContain("private-upstream-body", telemetry);
        Assert.DoesNotContain("api-key", telemetry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", telemetry, StringComparison.OrdinalIgnoreCase);

        capture.FlushMetrics();
        var input = Assert.Single(capture.ModelMeasurements, metric =>
            metric.Name == "gen_ai.client.token.usage" && metric.Tags.GetValueOrDefault("gen_ai.token.type") as string == "input");
        var output = Assert.Single(capture.ModelMeasurements, metric =>
            metric.Name == "gen_ai.client.token.usage" && metric.Tags.GetValueOrDefault("gen_ai.token.type") as string == "output");
        Assert.Equal(1234, input.Sum);
        Assert.Equal(2, output.Sum);
        Assert.Equal(1, input.Count);
        Assert.Equal("{token}", input.Unit);
        var duration = Assert.Single(capture.ModelMeasurements, metric => metric.Name == "gen_ai.client.operation.duration");
        Assert.Equal("s", duration.Unit);
        Assert.Equal(1, duration.Count);
        Assert.True(duration.Sum > 0);
        Assert.False(duration.Tags.ContainsKey("error.type"));
    }

    [Theory]
    [InlineData(401, 502)]
    [InlineData(429, 429)]
    [InlineData(500, 502)]
    public async Task ExportsActualUpstreamFailureCodeWithoutRetryOrInventedUsage(int upstream, int expected)
    {
        using var capture = new Capture();
        capture.Factory.Handler.StatusCode = (HttpStatusCode)upstream;
        using var client = capture.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest("PRIVATE-QUESTION-123"));
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(1, capture.Factory.Handler.Calls);
        var model = Assert.Single(capture.Spans, span => span.Source.Name == DecisionTelemetry.ModelSourceName);
        Assert.Equal(ActivityStatusCode.Error, model.Status);
        Assert.Equal(upstream.ToString(), model.GetTagItem("error.type"));
        Assert.Equal(upstream, model.GetTagItem("http.response.status_code"));
        Assert.Null(model.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.DoesNotContain("private-upstream-body", capture.SerializedSpans());
        capture.FlushMetrics();
        Assert.DoesNotContain(capture.ModelMeasurements, metric => metric.Name == "gen_ai.client.token.usage");
        var duration = Assert.Single(capture.ModelMeasurements, metric => metric.Name == "gen_ai.client.operation.duration");
        Assert.Equal(upstream.ToString(), duration.Tags["error.type"]);
        Assert.Equal(1, duration.Count);
    }

    [Theory]
    [InlineData(true, "timeout", 504)]
    [InlineData(false, "connection_error", 503)]
    public async Task ExportsTimeoutAndConnectionFailure(bool timeout, string errorType, int expected)
    {
        using var capture = new Capture();
        capture.Factory.Handler.Failure = timeout ? new TaskCanceledException() : new HttpRequestException();
        using var client = capture.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest("Una domanda"));
        Assert.Equal(expected, (int)response.StatusCode);
        var model = Assert.Single(capture.Spans, span => span.Source.Name == DecisionTelemetry.ModelSourceName);
        Assert.Equal(ActivityStatusCode.Error, model.Status);
        Assert.Equal(errorType, model.GetTagItem("error.type"));
        capture.FlushMetrics();
        Assert.Equal(errorType, Assert.Single(capture.ModelMeasurements).Tags["error.type"]);
    }

    [Fact]
    public async Task TracksUnavailableUsageWithoutEmittingZeroTokenMeasurements()
    {
        using var capture = new Capture();
        capture.Factory.Handler.ResponseBody = JsonSerializer.Serialize(DecisionResponseMapperTests.ValidPayload() with { Usage = null },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var client = capture.CreateClient();
        (await client.PostAsJsonAsync("/api/ratings", new RatingRequest("Una domanda"))).EnsureSuccessStatusCode();
        var model = Assert.Single(capture.Spans, span => span.Source.Name == DecisionTelemetry.ModelSourceName);
        Assert.Null(model.GetTagItem("gen_ai.usage.input_tokens"));
        capture.FlushMetrics();
        Assert.DoesNotContain(capture.ModelMeasurements, metric => metric.Name == "gen_ai.client.token.usage");
        var missing = Assert.Single(capture.ModelMeasurements, metric => metric.Name == "decision.client.responses_without_usage");
        Assert.Equal(1, missing.Sum);
    }

    [Fact]
    public async Task PreservesExplicitZeroUsageAsReportedMeasurements()
    {
        using var capture = new Capture();
        capture.Factory.Handler.ResponseBody = JsonSerializer.Serialize(
            DecisionResponseMapperTests.ValidPayload() with { Usage = new DecisionUsage(0, 0) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var client = capture.CreateClient();
        (await client.PostAsJsonAsync("/api/ratings", new RatingRequest("Una domanda"))).EnsureSuccessStatusCode();
        capture.FlushMetrics();
        var usage = capture.ModelMeasurements.Where(metric => metric.Name == "gen_ai.client.token.usage").ToArray();
        Assert.Equal(2, usage.Length);
        Assert.All(usage, metric =>
        {
            Assert.Equal(1, metric.Count);
            Assert.Equal(0, metric.Sum);
        });
        Assert.DoesNotContain(capture.ModelMeasurements, metric => metric.Name == "decision.client.responses_without_usage");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"usage":{"input_tokens":-1,"output_tokens":2}}""")]
    public async Task MarksInvalidJsonOrUsageWithoutExportingInvalidTokenMeasurements(string body)
    {
        using var capture = new Capture();
        capture.Factory.Handler.ResponseBody = body;
        using var client = capture.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest("Una domanda"));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var model = Assert.Single(capture.Spans, span => span.Source.Name == DecisionTelemetry.ModelSourceName);
        Assert.Equal(ActivityStatusCode.Error, model.Status);
        Assert.Equal("invalid_response", model.GetTagItem("error.type"));
        capture.FlushMetrics();
        Assert.DoesNotContain(capture.ModelMeasurements, metric => metric.Name == "gen_ai.client.token.usage");
    }

    [Fact]
    public async Task RecordsAuthenticationFailureBeforeAnyModelHttpRequest()
    {
        using var capture = new Capture(services =>
        {
            services.Configure<DecisionOptions>(options => options.ApiKey = null);
            services.RemoveAll<TokenCredential>();
            services.AddSingleton<TokenCredential, UnavailableCredential>();
        });
        using var client = capture.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest("Una domanda"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, capture.Factory.Handler.Calls);
        Assert.DoesNotContain(capture.Spans, span => span.Source.Name == DecisionTelemetry.ModelSourceName);
        var agent = Assert.Single(capture.Spans, span => span.Source.Name == DecisionTelemetry.AgentSourceName);
        Assert.Equal(ActivityStatusCode.Error, agent.Status);
        capture.FlushMetrics();
        Assert.Empty(capture.ModelMeasurements);
    }

    [Fact]
    public async Task RejectedQuestionDoesNotCreateAgentOrModelTelemetry()
    {
        using var capture = new Capture();
        using var client = capture.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest(""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(capture.Spans, span => span.Source.Name is
            DecisionTelemetry.AgentSourceName or DecisionTelemetry.ModelSourceName);
        capture.FlushMetrics();
        Assert.Empty(capture.ModelMeasurements);
    }

    [Fact]
    public async Task PreservesReportedTokensEvenWhenClassificationResponseIsInvalid()
    {
        using var capture = new Capture();
        capture.Factory.Handler.ResponseBody = JsonSerializer.Serialize(DecisionResponseMapperTests.ValidPayload() with { Answers = [] },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var client = capture.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest("Una domanda"));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var model = Assert.Single(capture.Spans, span => span.Source.Name == DecisionTelemetry.ModelSourceName);
        Assert.Equal(ActivityStatusCode.Error, model.Status);
        Assert.Equal("invalid_response", model.GetTagItem("error.type"));
        Assert.Equal(1234L, model.GetTagItem("gen_ai.usage.input_tokens"));
        capture.FlushMetrics();
        Assert.Equal(1236, capture.ModelMeasurements.Where(metric => metric.Name == "gen_ai.client.token.usage").Sum(metric => metric.Sum));
    }

    [Fact]
    public async Task MarksUserCancellationAndClosesModelSpanWithoutInventingUsage()
    {
        using var capture = new Capture();
        capture.Factory.Handler.WaitUntilCancelled = true;
        using var client = capture.CreateClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = client.PostAsJsonAsync("/api/ratings", new RatingRequest("Una domanda"), cancellation.Token);
        await capture.Factory.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await capture.TraceExporter.ModelStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var model = Assert.Single(capture.Spans, span => span.Source.Name == DecisionTelemetry.ModelSourceName);
        Assert.Equal(true, model.GetTagItem("decision.cancelled"));
        Assert.NotEqual(ActivityStatusCode.Error, model.Status);
        Assert.Null(model.GetTagItem("error.type"));
        Assert.Null(model.GetTagItem("gen_ai.usage.input_tokens"));
        capture.FlushMetrics();
        Assert.True((bool)Assert.Single(capture.ModelMeasurements).Tags["decision.cancelled"]!);
    }

    [Fact]
    public async Task CapturesMessagesOnlyWithExplicitSensitiveDataOptIn()
    {
        using var capture = new Capture(services => services.Configure<GenAiTelemetryOptions>(options => options.EnableSensitiveData = true));
        using var client = capture.CreateClient();
        (await client.PostAsJsonAsync("/api/ratings", new RatingRequest("OPT-IN-TEST-QUESTION"))).EnsureSuccessStatusCode();
        var telemetry = capture.SerializedSpans();
        Assert.Contains("OPT-IN-TEST-QUESTION", telemetry);
        Assert.DoesNotContain("PRIVATE-API-KEY-123", telemetry);
    }

    [Fact]
    public async Task PropagatesSameTraceThroughActualModelHttpRequest()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var modelServer = builder.Build();
        string? receivedTraceParent = null;
        modelServer.MapPost("/providers/microsoft/v1/systemone", (HttpContext context) =>
        {
            receivedTraceParent = context.Request.Headers["traceparent"];
            return Results.Json(DecisionResponseMapperTests.ValidPayload());
        });
        await modelServer.StartAsync();
        var address = new Uri(modelServer.Urls.Single());
        using var capture = new Capture(services =>
            services.AddHttpClient<DecisionClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler())
                .AddHttpMessageHandler(() => new LoopbackHandler(address)));
        using var client = capture.CreateClient();
        (await client.PostAsJsonAsync("/api/ratings", new RatingRequest("Una domanda"))).EnsureSuccessStatusCode();
        var model = Assert.Single(capture.Spans, span => span.Source.Name == DecisionTelemetry.ModelSourceName);
        var http = Assert.Single(capture.Spans, span => span.Kind == ActivityKind.Client &&
            span.Source.Name == "System.Net.Http" && span.GetTagItem("http.request.method") as string == "POST");
        Assert.Equal(model.SpanId, http.ParentSpanId);
        Assert.NotNull(receivedTraceParent);
        Assert.Contains(capture.TraceId.ToString(), receivedTraceParent);
        Assert.Contains(http.SpanId.ToString(), receivedTraceParent);
        await modelServer.StopAsync();
    }

    private sealed class LoopbackHandler(Uri address) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri(address, request.RequestUri!.AbsolutePath);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class UnavailableCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new AuthenticationFailedException("Credential unavailable in telemetry test.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromException<AccessToken>(new AuthenticationFailedException("Credential unavailable in telemetry test."));
    }

    private sealed class Capture : IDisposable
    {
        public ActivityTraceId TraceId { get; } = ActivityTraceId.CreateRandom();
        public TraceExporter TraceExporter { get; } = new();
        private readonly MetricExporter metricExporter = new();
        public ApiTests.TestApiFactory Factory { get; }
        public Activity[] Spans => TraceExporter.Items.Where(span => span.TraceId == TraceId).ToArray();
        public Measurement[] ModelMeasurements => metricExporter.Items.Where(metric => metric.Meter == DecisionTelemetry.ModelSourceName).ToArray();

        public Capture(Action<IServiceCollection>? configure = null)
        {
            Factory = new ApiTests.TestApiFactory(services =>
            {
                services.Configure<DecisionOptions>(options => options.ApiKey = "PRIVATE-API-KEY-123");
                services.AddOpenTelemetry()
                    .WithTracing(tracing => tracing.SetSampler(new AlwaysOnSampler())
                        .AddProcessor(new SimpleActivityExportProcessor(TraceExporter)))
                    .WithMetrics(metrics => metrics.AddReader(new PeriodicExportingMetricReader(
                        metricExporter, exportIntervalMilliseconds: int.MaxValue)));
                configure?.Invoke(services);
            });
        }

        public HttpClient CreateClient()
        {
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Add("traceparent", $"00-{TraceId}-{ActivitySpanId.CreateRandom()}-01");
            return client;
        }

        public string SerializedSpans() => JsonSerializer.Serialize(Spans.Select(span => new
        {
            span.DisplayName,
            span.TagObjects,
            Events = span.Events.Select(e => new { e.Name, e.Tags }),
            span.StatusDescription
        }));

        public void FlushMetrics() => Assert.True(Factory.Services.GetRequiredService<MeterProvider>().ForceFlush());
        public void Dispose() => Factory.Dispose();
    }

    private sealed class TraceExporter : BaseExporter<Activity>
    {
        public ConcurrentQueue<Activity> Items { get; } = new();
        public TaskCompletionSource ModelStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                Items.Enqueue(activity);
                if (activity.Source.Name == DecisionTelemetry.ModelSourceName)
                {
                    ModelStopped.TrySetResult();
                }
            }
            return ExportResult.Success;
        }
    }

    private sealed record Measurement(string Meter, string Name, string Unit, long Count, double Sum, Dictionary<string, object?> Tags);

    private sealed class MetricExporter : BaseExporter<Metric>
    {
        public ConcurrentQueue<Measurement> Items { get; } = new();

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (metric.MeterName != DecisionTelemetry.ModelSourceName)
                {
                    continue;
                }
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var tags = new Dictionary<string, object?>();
                    foreach (var tag in point.Tags)
                    {
                        tags.Add(tag.Key, tag.Value);
                    }
                    var histogram = metric.MetricType == MetricType.Histogram;
                    Items.Enqueue(new Measurement(metric.MeterName, metric.Name, metric.Unit,
                        histogram ? point.GetHistogramCount() : 1,
                        histogram ? point.GetHistogramSum() : point.GetSumLong(), tags));
                }
            }
            return ExportResult.Success;
        }
    }
}
