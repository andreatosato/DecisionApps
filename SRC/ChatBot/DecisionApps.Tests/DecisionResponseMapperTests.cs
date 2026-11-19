using DecisionApps.ApiService.Services;
using DecisionApps.Contracts;

namespace DecisionApps.Tests;

public sealed class DecisionResponseMapperTests
{
    private static readonly AgentCard[] Cards = new[] { "informativo", "crm", "scuola", "anagrafe", "tva" }
        .Select(id => new AgentCard(id, id, id)).ToArray();

    [Fact]
    public void MapsBothChoicesAndOrdersAgents()
    {
        var result = Map(ValidPayload());
        Assert.Equal("crm", result.SelectedAgent);
        Assert.Equal("Dispositiva", result.SelectedAction);
        Assert.Equal("crm", result.Agents[0].Id);
        Assert.Equal(80m, result.Agents[0].Percentage);
        Assert.Equal(100m, result.Agents.Sum(a => a.Percentage));
        Assert.Equal(100m, result.Actions.Sum(a => a.Percentage));
        Assert.False(result.NeedsReview);
        Assert.Equal(10, result.ElapsedMilliseconds);
        Assert.Equal(new TokenUsage(1234, 2), result.Usage);
        Assert.Equal(1236, result.Usage!.TotalTokens);
    }

    [Fact]
    public void RoundsWithExactlyOneHundredPercentAndPreservesSmallScores()
    {
        var payload = ValidPayload();
        payload.Answers!["agent"] = new ChoiceAnswer("choice", "scuola", new()
        {
            ["informativo"] = .33333m,
            ["crm"] = .33333m,
            ["scuola"] = .33334m,
            ["anagrafe"] = 0m,
            ["tva"] = 0m
        });
        var result = Map(payload);
        Assert.Equal(100m, result.Agents.Sum(a => a.Percentage));
        Assert.Equal(33.34m, result.Agents[0].Percentage);
        Assert.True(result.NeedsReview);
    }

    [Fact]
    public void AcceptsOnlyTinyNormalizationDrift()
    {
        var payload = ValidPayload();
        payload.Answers!["agent"].Probabilities!["crm"] = .80001m;
        var result = Map(payload);
        Assert.Equal(100m, result.Agents.Sum(a => a.Percentage));
        Assert.InRange(result.Agents.Sum(a => a.Probability), .999999999m, 1.000000001m);
    }

    [Theory]
    [InlineData(-.1)]
    [InlineData(1.1)]
    [InlineData(0)]
    [InlineData(.3)]
    public void RejectsInvalidAgentProbability(decimal probability)
    {
        var payload = ValidPayload();
        payload.Answers!["agent"].Probabilities!["crm"] = probability;
        Assert.Throws<DecisionException>(() => Map(payload));
    }

    [Fact]
    public void RejectsMissingOptionRatherThanInventingZero()
    {
        var payload = ValidPayload();
        payload.Answers!["agent"].Probabilities!.Remove("tva");
        Assert.Throws<DecisionException>(() => Map(payload));
    }

    [Fact]
    public void RejectsUnexpectedOption()
    {
        var payload = ValidPayload();
        payload.Answers!["agent"].Probabilities!["unknown"] = 0;
        Assert.Throws<DecisionException>(() => Map(payload));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    [InlineData("informativo")]
    public void RejectsMissingUnknownOrNonMaximumSelection(string? choice)
    {
        var payload = ValidPayload();
        payload.Answers!["agent"] = payload.Answers["agent"] with { Choice = choice };
        Assert.Throws<DecisionException>(() => Map(payload));
    }

    [Fact]
    public void RejectsMissingAnswers() =>
        Assert.Throws<DecisionException>(() => Map(new DecisionPayload(null, null)));

    [Fact]
    public void RejectsMissingAction()
    {
        var payload = ValidPayload();
        payload.Answers!.Remove("action");
        Assert.Throws<DecisionException>(() => Map(payload));
    }

    [Fact]
    public void RejectsWrongQuestionType()
    {
        var payload = ValidPayload();
        payload.Answers!["action"] = payload.Answers["action"] with { Type = "score" };
        Assert.Throws<DecisionException>(() => Map(payload));
    }

    [Fact]
    public void FlagsUncertainAction()
    {
        var payload = ValidPayload();
        payload.Answers!["action"] = new ChoiceAnswer("choice", "Informativa",
            new() { ["Informativa"] = .55m, ["Dispositiva"] = .45m });
        Assert.True(Map(payload).NeedsReview);
    }

    [Fact]
    public void FlagsSmallAgentMargin()
    {
        var payload = ValidPayload();
        payload.Answers!["agent"] = new ChoiceAnswer("choice", "crm",
            new() { ["informativo"] = .45m, ["crm"] = .50m, ["scuola"] = .05m, ["anagrafe"] = 0, ["tva"] = 0 });
        Assert.True(Map(payload).NeedsReview);
    }

    [Fact]
    public void LeavesMissingUsageUnavailableRatherThanInventingZero() =>
        Assert.Null(Map(ValidPayload() with { Usage = null }).Usage);

    [Fact]
    public void PreservesReportedZeroTokenUsage()
    {
        var result = Map(ValidPayload() with { Usage = new DecisionUsage(0, 0) });
        Assert.NotNull(result.Usage);
        Assert.Equal(0, result.Usage.TotalTokens);
    }

    [Theory]
    [InlineData(null, 1L)]
    [InlineData(1L, null)]
    [InlineData(-1L, 1L)]
    [InlineData(1L, -1L)]
    [InlineData(long.MaxValue, 1L)]
    public void RejectsIncompleteNegativeOrOverflowingUsage(long? input, long? output) =>
        Assert.Throws<DecisionException>(() => Map(ValidPayload() with { Usage = new DecisionUsage(input, output) }));

    private static RoutingResult Map(DecisionPayload payload) =>
        DecisionResponseMapper.Map(payload, Cards, 10, "Decision-1.1");

    internal static DecisionPayload ValidPayload() => new("Decision-1.1", new()
    {
        ["agent"] = new("choice", "crm", new()
        {
            ["informativo"] = .05m,
            ["crm"] = .8m,
            ["scuola"] = .05m,
            ["anagrafe"] = .05m,
            ["tva"] = .05m
        }),
        ["action"] = new("choice", "Dispositiva", new() { ["Informativa"] = .1m, ["Dispositiva"] = .9m })
    }, new DecisionUsage(1234, 2));
}
