using System.Net;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Net.Http.Headers;
using Azure.Core;
using Azure.Identity;
using DecisionApps.ApiService.Services;
using DecisionApps.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DecisionApps.Tests;

public sealed class ModelTimingTests
{
    [Theory]
    [InlineData(250)]
    [InlineData(0)]
    public async Task ExcludesCredentialAcquisitionFromModelRequestDuration(int milliseconds)
    {
        var clock = new ManualClock();
        using var factory = CreateFactory(clock);
        factory.Handler.OnSend = () => clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        var traceId = ActivityTraceId.CreateRandom();
        Activity? modelSpan = null;
        double? modelMetricMilliseconds = null;
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DecisionTelemetry.ModelSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == traceId) modelSpan = activity;
            }
        };
        ActivitySource.AddActivityListener(activityListener);
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == DecisionTelemetry.ModelSourceName &&
                instrument.Name == "gen_ai.client.operation.duration")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>((_, value, _, _) =>
        {
            if (Activity.Current?.TraceId == traceId) modelMetricMilliseconds = value * 1000;
        });
        meterListener.Start();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest("Una domanda"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RoutingResult>();
        Assert.Equal(5000 + milliseconds, clock.GetElapsedTime(0).TotalMilliseconds);
        Assert.Equal(milliseconds, result!.ElapsedMilliseconds);
        Assert.NotNull(modelSpan);
        Assert.Equal(milliseconds, (long)modelSpan.Duration.TotalMilliseconds);
        Assert.Equal(milliseconds, modelMetricMilliseconds);
    }

    [Fact]
    public async Task ReportsModelDurationForFailedRequestsWithoutIncludingAuthentication()
    {
        var clock = new ManualClock();
        using var factory = CreateFactory(clock);
        factory.Handler.OnSend = () => clock.Advance(TimeSpan.FromMilliseconds(250));
        factory.Handler.StatusCode = HttpStatusCode.TooManyRequests;
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest("Una domanda"));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(250, problem.RootElement.GetProperty("modelElapsedMilliseconds").GetInt64());
    }

    [Fact]
    public async Task IncludesCompleteHttpResponseBodyTransferInModelDuration()
    {
        var clock = new ManualClock();
        using var factory = CreateFactory(clock);
        factory.Handler.OnSend = () => clock.Advance(TimeSpan.FromMilliseconds(250));
        factory.Handler.ResponseContentFactory = () => new AdvancingContent(clock, factory.Handler.ResponseBody);
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest("Una domanda"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RoutingResult>();
        Assert.Equal(550, result!.ElapsedMilliseconds);
        Assert.Equal(5550, clock.GetElapsedTime(0).TotalMilliseconds);
    }

    [Fact]
    public async Task AuthenticationFailureDoesNotInventAModelRequestDuration()
    {
        var clock = new ManualClock();
        using var factory = CreateFactory(clock, failAuthentication: true);
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest("Una domanda"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, factory.Handler.Calls);
        using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.False(problem.RootElement.TryGetProperty("modelElapsedMilliseconds", out _));
    }

    private static ApiTests.TestApiFactory CreateFactory(ManualClock clock, bool failAuthentication = false) =>
        new(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<TokenCredential>();
            services.AddSingleton<TokenCredential>(new AdvancingCredential(clock, failAuthentication));
            services.Configure<DecisionOptions>(options => options.ApiKey = null);
        });

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref timestamp, duration.Ticks);
    }

    private sealed class AdvancingContent : HttpContent
    {
        private readonly ManualClock clock;
        private readonly byte[] body;

        public AdvancingContent(ManualClock clock, string body)
        {
            this.clock = clock;
            this.body = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            clock.Advance(TimeSpan.FromMilliseconds(300));
            return stream.WriteAsync(body).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return true;
        }
    }

    private sealed class AdvancingCredential(ManualClock clock, bool fail) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            return fail
                ? throw new AuthenticationFailedException("Authentication unavailable in timing test.")
                : new AccessToken("timing-test-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
