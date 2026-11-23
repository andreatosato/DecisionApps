using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pong.Web.Contracts;

namespace Pong.Tests;

public sealed class ApiTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectedGptUsesDecisionsThroughTheSameAgentAndReportsUsage(bool apiKey)
    {
        using var factory = new TestHost { EnableGpt = true, UseApiKey = apiKey };
        factory.Handler.ResponseBody = FakeHandler.ValidGptResponse;
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions",
            TestHost.ObservedState() with { Model = "gpt" });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<DecisionResult>();
        Assert.Equal("gpt-6-luna", result!.Deployment);
        Assert.Equal("up", result.Action);
        Assert.Equal(new TokenUsage(123, 0, 123), result.Usage);
        Assert.Equal(new ActionProbabilities(0.8m, 0.2m, 0), result.Probabilities);
        Assert.Equal("https://offline.invalid/openai/v1/decisions", factory.Handler.RequestUri);
        Assert.Equal(apiKey ? "offline-gpt-key" : null, factory.Handler.ApiKey);
        Assert.Equal(apiKey ? null : "Bearer offline-token", factory.Handler.Authorization);
        using var payload = JsonDocument.Parse(factory.Handler.RequestBody!);
        var root = payload.RootElement;
        Assert.Equal("gpt-6-luna", root.GetProperty("model").GetString());
        var question = Assert.Single(root.GetProperty("questions").EnumerateArray());
        Assert.Equal("choice", question.GetProperty("type").GetString());
        Assert.Equal("action", question.GetProperty("name").GetString());
        Assert.Contains("history", question.GetProperty("instructions").GetString());
        var choices = question.GetProperty("choices").EnumerateArray().ToArray();
        Assert.Equal(["up", "down", "stay"], choices.Select(choice => choice.GetProperty("value").GetString()));
        Assert.All(choices, choice => Assert.False(string.IsNullOrWhiteSpace(choice.GetProperty("description").GetString())));
        using var input = JsonDocument.Parse(root.GetProperty("input").GetString()!);
        Assert.Equal(2, input.RootElement.GetProperty("history").GetArrayLength());
        Assert.Equal(145, input.RootElement.GetProperty("estimatedResponseMilliseconds").GetDouble());
        Assert.False(root.TryGetProperty("messages", out _));
        Assert.False(root.TryGetProperty("response_format", out _));
        Assert.False(root.TryGetProperty("max_completion_tokens", out _));
        Assert.False(root.TryGetProperty("max_output_tokens", out _));
        Assert.Equal(1, factory.Handler.Calls);
    }

    [Theory]
    [InlineData("unknown", 400)]
    [InlineData("gpt", 503)]
    public async Task UnavailableModelNeverFallsBackToDecision(string model, int status)
    {
        using var factory = new TestHost();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State() with { Model = model });
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, factory.Handler.Calls);
        Assert.Equal(0, factory.Credential.Calls);
    }

    [Theory]
    [InlineData("""{"answers":[]}""")]
    [InlineData("""{"answers":[{"type":"refusal","name":"action"}]}""")]
    [InlineData("""{"answers":[{"type":"choice","name":"other"}]}""")]
    [InlineData("""{"answers":[{},{}]}""")]
    [InlineData("""{"answers":{"action":{"type":"choice"}}}""")]
    [InlineData("""{"error":{"code":"server_error"},"answers":[]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"{}"}}]}""")]
    public async Task InvalidGptResponsesAreExplicitErrorsWithoutFallback(string body)
    {
        using var factory = new TestHost { EnableGpt = true };
        factory.Handler.ResponseBody = body;
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State() with { Model = "gpt" });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, factory.Handler.Calls);
        Assert.DoesNotContain("\"action\"", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("\"input_tokens\":123", "\"input_tokens\":-1")]
    [InlineData("\"output_tokens\":0", "\"output_tokens\":null")]
    [InlineData("\"total_tokens\":123", "\"total_tokens\":124")]
    public async Task InvalidGptTokenUsageCannotBecomeValidMatchStatistics(string original, string replacement)
    {
        using var factory = new TestHost { EnableGpt = true };
        factory.Handler.ResponseBody = FakeHandler.ValidGptResponse.Replace(original, replacement);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State() with { Model = "gpt" });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, factory.Handler.Calls);
    }

    [Theory]
    [InlineData("\"choice\":\"up\"", "\"choice\":\"left\"")]
    [InlineData("\"choice\":\"up\"", "\"choice\":\"down\"")]
    [InlineData("\"value\":\"stay\"", "\"value\":\"up\"")]
    [InlineData("\"value\":\"stay\"", "\"value\":\"left\"")]
    [InlineData("\"probability\":0.8", "\"probability\":0.5")]
    [InlineData("\"probability\":0.8", "\"probability\":-1")]
    [InlineData("\"probability\":0.8", "\"probability\":null")]
    [InlineData("{\"value\":\"stay\",\"probability\":0},", "")]
    public async Task InvalidNativeGptProbabilitiesAreRejected(string original, string replacement)
    {
        using var factory = new TestHost { EnableGpt = true };
        factory.Handler.ResponseBody = FakeHandler.ValidGptResponse.Replace(original, replacement);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State() with { Model = "gpt" });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, factory.Handler.Calls);
    }

    [Theory]
    [InlineData("decision")]
    [InlineData("gpt")]
    public async Task StepModeReachesBothModelsWithExplicitFrozenInferenceInstructions(string model)
    {
        using var factory = new TestHost { EnableGpt = true };
        factory.Handler.ResponseBody = model == "gpt" ? FakeHandler.ValidGptResponse : FakeHandler.ValidResponse;
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions",
            TestHost.ObservedState() with { Model = model, PlayMode = "steps" });
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(factory.Handler.RequestBody!);
        var root = payload.RootElement;
        var instructions = model == "gpt"
            ? root.GetProperty("questions")[0].GetProperty("instructions").GetString()
            : root.GetProperty("questions").GetProperty("action").GetProperty("instructions").GetString();
        Assert.Contains("entire court is frozen during inference", instructions);
        using var forwarded = JsonDocument.Parse(model == "gpt"
            ? root.GetProperty("input").GetString()!
            : root.GetProperty("state").GetRawText());
        Assert.Equal("steps", forwarded.RootElement.GetProperty("playMode").GetString());
    }

    [Fact]
    public async Task UnknownPlayModeIsRejectedBeforeInference()
    {
        using var factory = new TestHost();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State() with { PlayMode = "unknown" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Handler.Calls);
    }

    [Fact]
    public async Task AgentReceivesObservedHistoryAndMeasuredLatencyAndCanChooseStay()
    {
        using var factory = new TestHost();
        factory.Handler.ResponseBody = """
            {"answers":{"action":{"type":"choice","choice":"stay","probabilities":{"up":0.1,"down":0.2,"stay":0.7}}}}
            """;
        using var client = factory.CreateClient();
        var state = TestHost.State();
        using var response = await client.PostAsJsonAsync("/api/decisions", new
        {
            state.Sequence, state.Arena, state.Ball, state.Paddle, state.DecisionIntervalSeconds,
            observedAtMilliseconds = 200,
            estimatedResponseMilliseconds = 145,
            history = new[]
            {
                new { timestampMilliseconds = 150, ballX = 464, ballY = 207.5,
                    velocityX = 320, velocityY = -150, paddleY = 300 },
                new { timestampMilliseconds = 200, ballX = 480, ballY = 200.0,
                    velocityX = 320, velocityY = -150, paddleY = 300 }
            }
        });
        response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("stay", result.RootElement.GetProperty("action").GetString());
        Assert.Equal(0.7m, result.RootElement.GetProperty("probabilities").GetProperty("stay").GetDecimal());
        using var nativeRequest = JsonDocument.Parse(factory.Handler.RequestBody!);
        var forwarded = nativeRequest.RootElement.GetProperty("state");
        Assert.Equal(200, forwarded.GetProperty("observedAtMilliseconds").GetDouble());
        Assert.Equal(145, forwarded.GetProperty("estimatedResponseMilliseconds").GetDouble());
        Assert.Equal(2, forwarded.GetProperty("history").GetArrayLength());
        Assert.Equal(464, forwarded.GetProperty("history")[0].GetProperty("ballX").GetDouble());
        Assert.DoesNotContain("intercept", forwarded.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("target", forwarded.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var action = nativeRequest.RootElement.GetProperty("questions").GetProperty("action");
        Assert.Equal(["up", "down", "stay"],
            action.GetProperty("criteria").EnumerateObject().Select(property => property.Name));
        Assert.Contains("history", action.GetProperty("instructions").GetString());
        Assert.Contains("estimatedResponseMilliseconds", action.GetProperty("instructions").GetString());
        Assert.Equal(1, factory.Handler.Calls);
    }

    public static IEnumerable<object[]> InvalidObservationStates()
    {
        var state = TestHost.ObservedState();
        var first = state.History![0];
        var latest = state.History[1];
        yield return [state with { History = [] }];
        yield return [state with { History = [null!, latest] }];
        yield return [state with { ObservedAtMilliseconds = null }];
        yield return [state with { History = null }];
        yield return [state with { ObservedAtMilliseconds = -1 }];
        yield return [state with { EstimatedResponseMilliseconds = -1 }];
        yield return [state with { EstimatedResponseMilliseconds = 10001 }];
        yield return [state with { History = [first with { TimestampMilliseconds = 201 }, latest] }];
        yield return [state with { History = [first with { TimestampMilliseconds = 200 }, latest] }];
        yield return [state with { History = [first with { TimestampMilliseconds = -1 }, latest] }];
        yield return [state with { ObservedAtMilliseconds = 2000,
            History = [first, latest with { TimestampMilliseconds = 2000 }] }];
        yield return [state with { History = Enumerable.Range(0, 6)
            .Select(i => latest with { TimestampMilliseconds = 150 + 10 * i }).ToArray() }];
        yield return [state with { History = [first, latest with { BallX = 481 }] }];
        yield return [state with { History = [first, latest with { TimestampMilliseconds = 199 }] }];
        yield return [state with { History = [first with { BallY = 601 }, latest] }];
        yield return [state with { History = [first with { VelocityX = 5001 }, latest] }];
        yield return [state with { History = [first with { PaddleY = 0 }, latest] }];
    }

    [Theory]
    [MemberData(nameof(InvalidObservationStates))]
    public async Task InvalidObservationHistoryDoesNotAuthenticateOrInfer(DecisionRequest state)
    {
        using var factory = new TestHost();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", state);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(0, factory.Credential.Calls);
        Assert.Equal(0, factory.Handler.Calls);
    }

    [Theory]
    [InlineData("timestampMilliseconds")]
    [InlineData("ballX")]
    [InlineData("ballY")]
    [InlineData("velocityX")]
    [InlineData("velocityY")]
    [InlineData("paddleY")]
    public async Task IncompleteObservationCannotBecomeAnInventedZero(string field)
    {
        var body = JsonSerializer.SerializeToNode(TestHost.ObservedState(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        body["history"]![0]!.AsObject().Remove(field);
        using var factory = new TestHost();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/decisions",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Credential.Calls);
        Assert.Equal(0, factory.Handler.Calls);
    }

    [Fact]
    public async Task FreshDecisionRequestsReuseAuthenticationButNotModelResults()
    {
        using var factory = new TestHost();
        using var client = factory.CreateClient();
        var firstState = TestHost.State(1);
        using var first = await client.PostAsJsonAsync("/api/decisions", firstState);
        first.EnsureSuccessStatusCode();
        var secondState = firstState with
        {
            Sequence = 2,
            Ball = firstState.Ball with { X = 650, Y = 280 }
        };
        using var second = await client.PostAsJsonAsync("/api/decisions", secondState);
        second.EnsureSuccessStatusCode();
        Assert.Equal(1, factory.Credential.Calls);
        Assert.Equal(2, factory.Handler.Calls);
        using var payload = JsonDocument.Parse(factory.Handler.RequestBody!);
        Assert.Equal(650, payload.RootElement.GetProperty("state").GetProperty("ball").GetProperty("x").GetDouble());
        var result = await second.Content.ReadFromJsonAsync<DecisionResult>();
        Assert.Equal(2, result!.Sequence);
    }

    [Theory]
    [InlineData(479, 320)]
    [InlineData(700, -320)]
    [InlineData(300, -320)]
    [InlineData(700, 0)]
    [InlineData(912, 320)]
    public async Task OutOfTurnStateDoesNotInvokeAgentOrModel(double x, double velocityX)
    {
        using var factory = new TestHost();
        var agentStarted = false;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Pong.Agents",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ => agentStarted = true
        };
        ActivitySource.AddActivityListener(listener);
        using var client = factory.CreateClient();
        var state = TestHost.State();
        state = state with { Ball = state.Ball with { X = x, VelocityX = velocityX } };
        using var response = await client.PostAsJsonAsync("/api/decisions", state);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Handler.Calls);
        Assert.Equal(0, factory.Credential.Calls);
        Assert.False(agentStarted);
    }

    [Theory]
    [InlineData("{\"sequence\":0}")]
    [InlineData("{")]
    [InlineData("{\"sequence\":\"wrong-type\"}")]
    [InlineData("{\"sequence\":0,\"arena\":42}")]
    [InlineData("{\"sequence\":0,\"arena\":{\"width\":960}}")]
    [InlineData("null")]
    [InlineData("{\"sequence\":0,\"arena\":null,\"ball\":null,\"paddle\":null,\"decisionIntervalSeconds\":0.35}")]
    public async Task DevelopmentBindingFailuresAre400WithoutInference(string body)
    {
        using var factory = new TestHost { EnvironmentName = "Development" };
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/decisions",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(0, factory.Handler.Calls);
        Assert.Equal(0, factory.Credential.Calls);
    }

    [Fact]
    public async Task CallsAgentAndNativeSystemOneWithFullNumericState()
    {
        using var factory = new TestHost();
        var agentStarted = false;
        var modelStarted = false;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Pong.Agents" or "Pong.Models",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (activity.Source.Name == "Pong.Agents") agentStarted = true;
                if (activity.Source.Name == "Pong.Models") modelStarted = true;
            }
        };
        ActivitySource.AddActivityListener(listener);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State());
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<DecisionResult>();
        Assert.NotNull(result);
        Assert.Equal(5, result.Sequence);
        Assert.Equal("up", result.Action);
        Assert.Equal(new ActionProbabilities(0.8m, 0.2m, 0m), result.Probabilities);
        Assert.Equal("Decision-1.1", result.Deployment);
        Assert.Equal(new TokenUsage(123, 2, 125), result.Usage);
        Assert.True(result.ElapsedMilliseconds >= 0);
        Assert.True(agentStarted);
        Assert.True(modelStarted);
        Assert.Equal(1, factory.Handler.Calls);
        Assert.Equal(1, factory.Credential.Calls);
        Assert.Equal(["https://cognitiveservices.azure.com/.default"], factory.Credential.Scopes!);
        Assert.Equal("Bearer offline-token", factory.Handler.Authorization);
        Assert.Null(factory.Handler.ApiKey);
        Assert.Equal(Encoding.UTF8.GetByteCount(factory.Handler.RequestBody!), factory.Handler.ContentLength);
        Assert.Equal(factory.Endpoint, factory.Handler.RequestUri);
        using var body = JsonDocument.Parse(factory.Handler.RequestBody!);
        var root = body.RootElement;
        Assert.Equal("Decision-1.1", root.GetProperty("model").GetString());
        var state = root.GetProperty("state");
        Assert.Equal(320, state.GetProperty("ball").GetProperty("velocityX").GetDouble());
        Assert.Equal(-150, state.GetProperty("ball").GetProperty("velocityY").GetDouble());
        Assert.Equal(926, state.GetProperty("paddle").GetProperty("x").GetDouble());
        Assert.Equal(300, state.GetProperty("paddle").GetProperty("y").GetDouble());
        Assert.Equal(960, state.GetProperty("arena").GetProperty("width").GetDouble());
        Assert.Equal(0.35, state.GetProperty("decisionIntervalSeconds").GetDouble());
        Assert.DoesNotContain("target", state.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var questions = root.GetProperty("questions");
        Assert.Single(questions.EnumerateObject());
        var action = questions.GetProperty("action");
        Assert.Equal("choice", action.GetProperty("type").GetString());
        Assert.Equal(["up", "down", "stay"], action.GetProperty("criteria").EnumerateObject().Select(property => property.Name));
        Assert.Contains("bounces", action.GetProperty("instructions").GetString());
        Assert.Contains("velocityX and velocityY", action.GetProperty("instructions").GetString());
        Assert.Contains("right half", action.GetProperty("instructions").GetString());
        Assert.DoesNotContain("prepare for its return", action.GetProperty("instructions").GetString());
        Assert.DoesNotContain("messages", root.GetRawText());
    }

    [Fact]
    public async Task ApiActuallyInvokesRegisteredAiAgent()
    {
        using var factory = new TestHost
        {
            CustomizeServices = services =>
            {
                services.RemoveAll<AIAgent>();
                services.AddTransient<AIAgent>(_ => throw new AgentResolutionException());
            }
        };
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State());
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, factory.Handler.Calls);
    }

    [Fact]
    public async Task ApiKeyStaysOnServerAndBypassesTokenCredential()
    {
        using var factory = new TestHost { UseApiKey = true };
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State());
        response.EnsureSuccessStatusCode();
        Assert.Equal("offline-test-key", factory.Handler.ApiKey);
        Assert.Null(factory.Handler.Authorization);
        Assert.Equal(0, factory.Credential.Calls);
        var configuration = await client.GetStringAsync("/api/configuration");
        Assert.DoesNotContain("offline-test-key", configuration);
        using var document = JsonDocument.Parse(configuration);
        Assert.Equal(3, document.RootElement.EnumerateObject().Count());
        Assert.Equal("API key", document.RootElement.GetProperty("authentication").GetString());
        Assert.Equal("Decision-1.1", document.RootElement.GetProperty("deployment").GetString());
        var models = document.RootElement.GetProperty("models");
        Assert.Equal(2, models.GetArrayLength());
        Assert.False(models[1].GetProperty("available").GetBoolean());
        Assert.Equal("DECISIONS API", models[1].GetProperty("protocol").GetString());
        Assert.DoesNotContain("offline-gpt-key", configuration);
    }

    [Fact]
    public async Task HealthWorksWithoutInference()
    {
        using var factory = new TestHost();
        using var client = factory.CreateClient();
        Assert.Equal("Healthy", await client.GetStringAsync("/health"));
        Assert.Equal("Healthy", await client.GetStringAsync("/alive"));
        Assert.Equal(0, factory.Handler.Calls);
        Assert.Equal(0, factory.Credential.Calls);
    }

    [Theory]
    [InlineData(401, 502)]
    [InlineData(403, 502)]
    [InlineData(404, 502)]
    [InlineData(429, 429)]
    [InlineData(500, 502)]
    [InlineData(503, 503)]
    [InlineData(504, 504)]
    [InlineData(408, 504)]
    public async Task UpstreamFailureIsProblemDetailsWithNoRetryOrFallback(int upstream, int expected)
    {
        using var factory = new TestHost();
        factory.Handler.StatusCode = (HttpStatusCode)upstream;
        factory.Handler.ResponseBody = "private-upstream-body";
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State());
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-upstream-body", body);
        Assert.DoesNotContain("\"action\"", body);
        Assert.Equal(1, factory.Handler.Calls);
        if (expected == 429) Assert.NotNull(response.Headers.RetryAfter);
    }

    [Theory]
    [InlineData(true, 504)]
    [InlineData(false, 503)]
    public async Task MapsTransportAndTimeoutFailures(bool timeout, int expected)
    {
        using var factory = new TestHost();
        factory.Handler.Failure = timeout ? new TaskCanceledException() : new HttpRequestException();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State());
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(1, factory.Handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthenticationFailuresAre503WithoutSendingInference(bool unavailable)
    {
        using var factory = new TestHost();
        factory.Credential.Failure = unavailable ? new CredentialUnavailableException("private credential details")
            : new AuthenticationFailedException("private credential details");
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("private credential details", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationReachesTransportAndIsNotTimeoutProblem()
    {
        using var factory = new TestHost();
        factory.Handler.WaitUntilCancelled = true;
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = client.PostAsJsonAsync("/api/decisions", TestHost.State(), cancellation.Token);
        await factory.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await factory.Handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, factory.Handler.Calls);
    }

    [Fact]
    public async Task BoundedConcurrencyRejectsBusyRequestsAndReleasesCancelledPermits()
    {
        using var factory = new TestHost();
        factory.Handler.WaitUntilCancelled = true;
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var first = client.PostAsJsonAsync("/api/decisions", TestHost.State(), cancellation.Token);
        var second = client.PostAsJsonAsync("/api/decisions", TestHost.State(6), cancellation.Token);
        try
        {
            await factory.Handler.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var busy = await client.PostAsJsonAsync("/api/decisions", TestHost.State(7));
            Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), busy.Headers.RetryAfter!.Delta);
            Assert.Equal(2, factory.Handler.Calls);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        }
        factory.Handler.WaitUntilCancelled = false;
        using var next = await client.PostAsJsonAsync("/api/decisions", TestHost.State(8));
        next.EnsureSuccessStatusCode();
        Assert.Equal(8, (await next.Content.ReadFromJsonAsync<DecisionResult>())!.Sequence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"sequence\":-1}")]
    [InlineData("{\"sequence\":1.5}")]
    [InlineData("{\"sequence\":9223372036854775808}")]
    [InlineData("not json")]
    public async Task MalformedInputNeverCallsInference(string json)
    {
        using var factory = new TestHost();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/decisions", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Handler.Calls);
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("arena")]
    [InlineData("ball")]
    [InlineData("paddle")]
    [InlineData("decisionIntervalSeconds")]
    [InlineData("ball.velocityX")]
    public async Task MissingRequiredFieldsAreRejected(string field)
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(TestHost.State(), new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        var parts = field.Split('.');
        if (parts.Length == 1) body.AsObject().Remove(field);
        else body[parts[0]]!.AsObject().Remove(parts[1]);
        using var factory = new TestHost();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/decisions", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Handler.Calls);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"stay\",\"probabilities\":{\"up\":0.5,\"down\":0.5}}}}")]
    public async Task InvalidModelResponsesAre502(string payload)
    {
        using var factory = new TestHost();
        factory.Handler.ResponseBody = payload;
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State());
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, factory.Handler.Calls);
    }

    [Fact]
    public async Task OversizedResponseIs502AndNeverReturnedAsADecision()
    {
        using var factory = new TestHost();
        factory.Handler.ResponseBody = new string('x', 64 * 1024 + 1);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State());
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, factory.Handler.Calls);
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    public async Task OversizedInputIsRejectedBeforeCallingInference(string environment)
    {
        using var factory = new TestHost { EnvironmentName = environment };
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/decisions",
            new StringContent(new string(' ', 8193), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, factory.Handler.Calls);
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    public async Task WrongContentTypeIs415WithoutInference(string environment)
    {
        using var factory = new TestHost { EnvironmentName = environment };
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/decisions",
            new StringContent("{\"sequence\":0}", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(0, factory.Handler.Calls);
        Assert.Equal(0, factory.Credential.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(5001)]
    public async Task InvalidStateIs400BeforeInference(int speed)
    {
        using var factory = new TestHost();
        using var client = factory.CreateClient();
        var state = TestHost.State();
        using var response = await client.PostAsJsonAsync("/api/decisions",
            state with { Paddle = state.Paddle with { Speed = speed } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Handler.Calls);
    }

    [Theory]
    [InlineData("\"NaN\"")]
    [InlineData("\"300\"")]
    [InlineData("1e400")]
    public async Task NonNumericOrNonFiniteValuesAre400(string speed)
    {
        var state = TestHost.State();
        var body = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Replace("\"speed\":300", "\"speed\":" + speed, StringComparison.Ordinal);
        using var factory = new TestHost();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/decisions", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Handler.Calls);
    }

    [Fact]
    public async Task IndependentCallsEchoSequenceAndDoNotSendHistory()
    {
        using var factory = new TestHost();
        using var client = factory.CreateClient();
        foreach (var sequence in new long[] { 10, 0, 10 })
        {
            using var response = await client.PostAsJsonAsync("/api/decisions", TestHost.State(sequence));
            response.EnsureSuccessStatusCode();
            Assert.Equal(sequence, (await response.Content.ReadFromJsonAsync<DecisionResult>())!.Sequence);
            using var body = JsonDocument.Parse(factory.Handler.RequestBody!);
            Assert.Equal(sequence, body.RootElement.GetProperty("state").GetProperty("sequence").GetInt64());
            Assert.False(body.RootElement.TryGetProperty("messages", out _));
            Assert.False(body.RootElement.TryGetProperty("session", out _));
        }
        Assert.Equal(3, factory.Handler.Calls);
    }

    [Theory]
    [InlineData("http://offline.invalid/providers/microsoft/v1/systemone")]
    [InlineData("https://offline.invalid/chat/completions")]
    [InlineData("https://offline.invalid/providers/microsoft/v1/systemone?api-key=bad")]
    public void InvalidConfigurationFailsStartup(string endpoint)
    {
        using var factory = new TestHost { Endpoint = endpoint };
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => factory.CreateClient());
    }

    private sealed class AgentResolutionException : Exception;
}
