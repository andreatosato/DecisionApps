namespace DecisionApps.Contracts;

public enum EvaluationFilter
{
    All,
    NeedsReview,
    ScopeMismatch
}

public sealed record AgentComparison(string? ExpectedAgent, string? SelectedAgent, bool? Matches);
public sealed record EvaluationRow(ImportedQuestion Question, RoutingResult? Result, string? Error)
{
    public AgentComparison Comparison => RoutingEvaluation.Compare(Question, Result);

    public bool MatchesFilter(EvaluationFilter filter) => filter switch
    {
        EvaluationFilter.All => true,
        EvaluationFilter.NeedsReview => Result?.NeedsReview == true,
        EvaluationFilter.ScopeMismatch => Comparison.Matches is false,
        _ => throw new ArgumentOutOfRangeException(nameof(filter))
    };
}

public sealed record EvaluationSummary(
    int Matches, int Mismatches, int NotComparable, int Pending, int Errors)
{
    public int Compared => Matches + Mismatches;
    public decimal? MatchPercentage => Compared == 0 ? null : decimal.Round(Matches * 100m / Compared, 2);
}

public static class RoutingEvaluation
{
    public static AgentComparison Compare(ImportedQuestion question, RoutingResult? result) =>
        new(question.ExpectedAgent, result?.SelectedAgent,
            question.ExpectedAgent is null || result is null ? null :
                string.Equals(question.ExpectedAgent, result.SelectedAgent, StringComparison.Ordinal));

    public static EvaluationSummary Summarize(IEnumerable<EvaluationRow> rows)
    {
        int matches = 0, mismatches = 0, notComparable = 0, pending = 0, errors = 0;
        foreach (var row in rows)
        {
            if (row.Result is null)
            {
                if (row.Error is not null) errors++;
                else pending++;
            }
            else if (row.Comparison.Matches is not { } match)
            {
                notComparable++;
            }
            else if (match)
            {
                matches++;
            }
            else
            {
                mismatches++;
            }
        }
        return new EvaluationSummary(matches, mismatches, notComparable, pending, errors);
    }
}
