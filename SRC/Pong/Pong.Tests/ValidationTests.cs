using System.Text.Json;
using Pong.Web.Contracts;
using Pong.Web.Decision;
using Pong.Web.Game;

namespace Pong.Tests;

public sealed class ValidationTests
{
    [Theory]
    [InlineData("https://offline.invalid/openai/v1/chat/completions", true)]
    [InlineData("https://offline.invalid/openai/v1/responses", false)]
    [InlineData("http://offline.invalid/openai/v1/chat/completions", false)]
    [InlineData("https://offline.invalid/openai/v1/chat/completions?api-key=test", false)]
    public void GptAcceptsOnlyTheConfiguredChatCompletionsProtocol(string endpoint, bool valid)
    {
        Assert.Equal(valid, GptOptions.IsValid(new() { Deployment = "gpt-6-luna", Endpoint = endpoint }));
    }

    [Fact]
    public void AcceptsValidStateAndDifferentArenaDimensions()
    {
        Assert.True(StateValidator.IsValid(TestHost.State()));
        Assert.True(StateValidator.IsValid(new(0, new(640, 480), new(8, 472, -5000, 5000, 8),
            new(620, 44, 14, 88, 300), 0.35)));
        Assert.True(StateValidator.IsValid(TestHost.ObservedState()));
        Assert.True(StateValidator.IsValid(TestHost.ObservedState() with { EstimatedResponseMilliseconds = null }));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsNonFiniteObservationValuesAndLatency(double value)
    {
        var state = TestHost.ObservedState();
        var point = state.History![0];
        var latest = state.History[1];
        foreach (var invalid in new[]
        {
            point with { TimestampMilliseconds = value }, point with { BallX = value },
            point with { BallY = value }, point with { VelocityX = value },
            point with { VelocityY = value }, point with { PaddleY = value }
        })
        {
            Assert.False(StateValidator.IsValid(state with { History = [invalid, latest] }));
        }
        Assert.False(StateValidator.IsValid(state with { ObservedAtMilliseconds = value }));
        Assert.False(StateValidator.IsValid(state with { EstimatedResponseMilliseconds = value }));
    }

    public static IEnumerable<object[]> InvalidStates()
    {
        var state = TestHost.State();
        yield return [state with { Sequence = -1 }];
        yield return [state with { Arena = null! }];
        yield return [state with { Ball = null! }];
        yield return [state with { Paddle = null! }];
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            yield return [state with { Arena = state.Arena with { Width = value } }];
            yield return [state with { Arena = state.Arena with { Height = value } }];
            yield return [state with { Ball = state.Ball with { X = value } }];
            yield return [state with { Ball = state.Ball with { Y = value } }];
            yield return [state with { Ball = state.Ball with { VelocityX = value } }];
            yield return [state with { Ball = state.Ball with { VelocityY = value } }];
            yield return [state with { Ball = state.Ball with { Radius = value } }];
            yield return [state with { Paddle = state.Paddle with { X = value } }];
            yield return [state with { Paddle = state.Paddle with { Y = value } }];
            yield return [state with { Paddle = state.Paddle with { Width = value } }];
            yield return [state with { Paddle = state.Paddle with { Height = value } }];
            yield return [state with { Paddle = state.Paddle with { Speed = value } }];
            yield return [state with { DecisionIntervalSeconds = value }];
        }
        yield return [state with { Arena = new(0, 600) }];
        yield return [state with { Arena = new(960, 100000) }];
        yield return [state with { Ball = state.Ball with { X = 0 } }];
        yield return [state with { Ball = state.Ball with { X = 961 } }];
        yield return [state with { Ball = state.Ball with { Y = 7 } }];
        yield return [state with { Ball = state.Ball with { Y = 593 } }];
        yield return [state with { Ball = state.Ball with { Radius = 0 } }];
        yield return [state with { Ball = state.Ball with { Radius = 151 } }];
        yield return [state with { Ball = state.Ball with { VelocityX = -5001 } }];
        yield return [state with { Ball = state.Ball with { VelocityY = 5001 } }];
        yield return [state with { Paddle = state.Paddle with { X = 0 } }];
        yield return [state with { Paddle = state.Paddle with { X = 954 } }];
        yield return [state with { Paddle = state.Paddle with { Y = 43 } }];
        yield return [state with { Paddle = state.Paddle with { Y = 557 } }];
        yield return [state with { Paddle = state.Paddle with { Width = -1 } }];
        yield return [state with { Paddle = state.Paddle with { Height = 601 } }];
        yield return [state with { Paddle = state.Paddle with { Speed = 0 } }];
        yield return [state with { Paddle = state.Paddle with { Speed = 5001 } }];
        yield return [state with { DecisionIntervalSeconds = 0 }];
        yield return [state with { DecisionIntervalSeconds = 2.01 }];
    }

    [Theory]
    [MemberData(nameof(InvalidStates))]
    public void RejectsInvalidGeometry(DecisionRequest state) => Assert.False(StateValidator.IsValid(state));

    [Fact]
    public void RejectsNullState() => Assert.False(StateValidator.IsValid(null));

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"answers\":null}")]
    [InlineData("{\"answers\":{\"action\":null}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"text\",\"choice\":\"up\",\"probabilities\":{\"up\":1,\"down\":0,\"stay\":0}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"UP\",\"probabilities\":{\"up\":1,\"down\":0,\"stay\":0}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"left\",\"probabilities\":{\"up\":1,\"down\":0,\"stay\":0}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"up\"}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"up\",\"probabilities\":{\"up\":1}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"up\",\"probabilities\":{\"up\":1,\"down\":0}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"up\",\"probabilities\":{\"up\":1,\"down\":0,\"stay\":0,\"other\":0}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"up\",\"probabilities\":{\"up\":0.5,\"up\":0.5,\"stay\":0}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"up\",\"probabilities\":{\"up\":1.1,\"down\":-0.1,\"stay\":0}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"up\",\"probabilities\":{\"up\":0,\"down\":0,\"stay\":0}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"up\",\"probabilities\":{\"up\":0.7,\"down\":0.1,\"stay\":0.1}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"up\",\"probabilities\":{\"up\":0.4,\"down\":0.6,\"stay\":0}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"down\",\"probabilities\":{\"up\":0.8,\"down\":0.2,\"stay\":0}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"up\",\"probabilities\":{\"up\":0.3,\"down\":0.2,\"stay\":0.5}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"stay\",\"probabilities\":{\"up\":0.5,\"down\":0.4,\"stay\":0.1}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"up\",\"probabilities\":{\"up\":\"1\",\"down\":0,\"stay\":0}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"up\",\"probabilities\":{\"up\":1e100,\"down\":0,\"stay\":0}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"stay\",\"probabilities\":{\"up\":0,\"down\":0,\"stay\":\"1\"}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"stay\",\"probabilities\":{\"up\":0,\"down\":0,\"stay\":1.1}}}}")]
    [InlineData("{\"answers\":{\"action\":{\"type\":\"choice\",\"choice\":\"up\",\"probabilities\":{\"up\":1,\"down\":0.1,\"stay\":-0.1}}}}")]
    public void RejectsMalformedActionsAndProbabilities(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        Assert.Throws<DecisionException>(() => DecisionResponseMapper.Map(document.RootElement, 1, 0, "test"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("42")]
    [InlineData("{\"input_tokens\":-1,\"output_tokens\":2}")]
    [InlineData("{\"input_tokens\":1,\"output_tokens\":null}")]
    [InlineData("{\"input_tokens\":1.5,\"output_tokens\":1}")]
    [InlineData("{\"input_tokens\":\"1\",\"output_tokens\":1}")]
    [InlineData("{\"input_tokens\":9223372036854775807,\"output_tokens\":1}")]
    [InlineData("{\"input_tokens\":9223372036854775808,\"output_tokens\":0}")]
    [InlineData("{\"input_tokens\":1,\"output_tokens\":2,\"total_tokens\":4}")]
    [InlineData("{\"input_tokens\":1,\"output_tokens\":2,\"total_tokens\":null}")]
    public void RejectsNonsensicalUsage(string usage)
    {
        using var document = JsonDocument.Parse("""
            {"answers":{"action":{"type":"choice","choice":"up","probabilities":{"up":1,"down":0,"stay":0}}},"usage":
            """ + usage + "}");
        Assert.Throws<DecisionException>(() => DecisionResponseMapper.Map(document.RootElement, 1, 0, "test"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"usage\":null")]
    public void MissingUsageIsNull(string usage)
    {
        using var document = JsonDocument.Parse("""
            {"answers":{"action":{"type":"choice","choice":"down","probabilities":{"up":0,"down":1,"stay":0}}}
            """ + usage + "}");
        var result = DecisionResponseMapper.Map(document.RootElement, 9, 3, "test");
        Assert.Null(result.Usage);
        Assert.Equal("down", result.Action);
    }

    [Fact]
    public void DerivesUsageTotalAndNormalizesSmallRoundingError()
    {
        using var document = JsonDocument.Parse("""
            {"answers":{"action":{"type":"choice","choice":"up","probabilities":{"up":0.6666,"down":0.3333,"stay":0}}},
             "usage":{"input_tokens":0,"output_tokens":0}}
            """);
        var result = DecisionResponseMapper.Map(document.RootElement, 19, 42, "test");
        Assert.Equal(1m, result.Probabilities.Up + result.Probabilities.Down + result.Probabilities.Stay);
        Assert.Equal(new TokenUsage(0, 0, 0), result.Usage);
        Assert.Equal(19, result.Sequence);
        Assert.Equal(42, result.ElapsedMilliseconds);
    }
}
