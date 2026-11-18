using System.Text.Json;
using DecisionApps.Contracts;

namespace DecisionApps.Tests;

public sealed class RoutingEvaluationTests
{
    [Theory]
    [InlineData(EvaluationFilter.All, 1, 2, 3, 4, 5, 6, 7)]
    [InlineData(EvaluationFilter.NeedsReview, 1, 3, 4)]
    [InlineData(EvaluationFilter.ScopeMismatch, 2, 3)]
    public void FiltersResultsWithoutConfusingReviewMismatchAndUnavailable(EvaluationFilter filter, params int[] expectedIds)
    {
        EvaluationRow[] rows =
        [
            new(Question("crm") with { Id = 1 }, Result("crm") with { NeedsReview = true }, null),
            new(Question("anagrafe") with { Id = 2 }, Result("crm"), null),
            new(Question("informativo") with { Id = 3 }, Result("crm") with { NeedsReview = true }, null),
            new(Question(null) with { Id = 4 }, Result("tva") with { NeedsReview = true }, null),
            new(Question("crm") with { Id = 5 }, null, null),
            new(Question("crm") with { Id = 6 }, null, "Servizio non disponibile"),
            new(Question("crm") with { Id = 7 }, Result("crm"), null)
        ];
        Assert.Equal(expectedIds, rows.Where(row => row.MatchesFilter(filter)).Select(row => row.Question.Id));
    }

    [Theory]
    [InlineData(EvaluationFilter.NeedsReview)]
    [InlineData(EvaluationFilter.ScopeMismatch)]
    public void RecomputesFilterWhenResultIsReplaced(EvaluationFilter filter)
    {
        var row = new EvaluationRow(Question("crm"), Result("tva") with { NeedsReview = true }, null);
        Assert.True(row.MatchesFilter(filter));
        row = row with { Result = Result("crm") };
        Assert.False(row.MatchesFilter(filter));
    }

    [Fact]
    public void RejectsUnknownFilter() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvaluationRow(Question("crm"), Result("crm"), null).MatchesFilter((EvaluationFilter)99));

    [Fact]
    public void MatchesSelectedAgentNotJustAnyRatedAgent()
    {
        var result = Result("crm");
        var match = RoutingEvaluation.Compare(Question("crm"), result);
        Assert.True(match.Matches);
        Assert.Equal("crm", match.ExpectedAgent);
        Assert.Equal("crm", match.SelectedAgent);
        var mismatch = RoutingEvaluation.Compare(Question("informativo"), result);
        Assert.False(mismatch.Matches);
        Assert.Equal("informativo", mismatch.ExpectedAgent);
        Assert.Equal("crm", mismatch.SelectedAgent);
    }

    [Fact]
    public void DoesNotCompareWithoutExpectedAgent()
    {
        var comparison = RoutingEvaluation.Compare(Question(null), Result("crm"));
        Assert.Null(comparison.Matches);
        Assert.Null(comparison.ExpectedAgent);
        Assert.Equal("crm", comparison.SelectedAgent);
    }

    [Fact]
    public void DoesNotCountPendingAsMismatch()
    {
        var comparison = RoutingEvaluation.Compare(Question("crm"), null);
        Assert.Null(comparison.Matches);
        Assert.Equal("crm", comparison.ExpectedAgent);
        Assert.Null(comparison.SelectedAgent);
    }

    [Fact]
    public void ExcludesPendingErrorsAndUnmappedResultsFromDenominator()
    {
        EvaluationRow[] rows =
        [
            new(Question("crm"), Result("crm"), null),
            new(Question("crm"), Result("crm"), null),
            new(Question("informativo"), Result("crm"), null),
            new(Question("tva"), null, null),
            new(Question("scuola"), null, "Servizio non disponibile"),
            new(Question(null), Result("crm"), null),
            new(Question(null), null, null)
        ];
        var summary = RoutingEvaluation.Summarize(rows);
        Assert.Equal(2, summary.Matches);
        Assert.Equal(1, summary.Mismatches);
        Assert.Equal(3, summary.Compared);
        Assert.Equal(66.67m, summary.MatchPercentage);
        Assert.Equal(1, summary.NotComparable);
        Assert.Equal(2, summary.Pending);
        Assert.Equal(1, summary.Errors);
    }

    [Fact]
    public void ReportsNoPercentageUntilAtLeastOneComparableResult()
    {
        Assert.Null(RoutingEvaluation.Summarize([]).MatchPercentage);
        var summary = RoutingEvaluation.Summarize(
        [
            new(Question(null), Result("crm"), null),
            new(Question("crm"), null, null),
            new(Question("scuola"), null, "Errore")
        ]);
        Assert.Null(summary.MatchPercentage);
        Assert.Equal(0, summary.Compared);
    }

    [Theory]
    [InlineData("crm", 100)]
    [InlineData("tva", 0)]
    public void DistinguishesZeroPercentFromNoComparison(string expected, int percentage)
    {
        var summary = RoutingEvaluation.Summarize([new(Question(expected), Result("crm"), null)]);
        Assert.Equal((decimal)percentage, summary.MatchPercentage);
    }

    [Fact]
    public void ReplacingResultRecomputesComparisonWithoutStaleState()
    {
        var row = new EvaluationRow(Question("crm"), Result("tva"), null);
        Assert.False(row.Comparison.Matches);
        row = row with { Result = Result("crm") };
        Assert.True(row.Comparison.Matches);
        Assert.Equal(100m, RoutingEvaluation.Summarize([row]).MatchPercentage);
    }

    [Fact]
    public void SerializesComparisonWithNullableMatches()
    {
        var row = new EvaluationRow(Question(null), Result("crm"), null);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(row, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("comparison").GetProperty("matches").ValueKind);
        Assert.Equal("crm", json.RootElement.GetProperty("comparison").GetProperty("selectedAgent").GetString());
    }

    private static ImportedQuestion Question(string? expectedAgent) =>
        new(1, "Campione", 2, "Una domanda", expectedAgent is null ? null : "Ambito test", expectedAgent, "Foglio origine");

    private static RoutingResult Result(string selectedAgent) =>
        new(selectedAgent, "Informativa", [], [], false, 1, "Decision-1.1");
}
