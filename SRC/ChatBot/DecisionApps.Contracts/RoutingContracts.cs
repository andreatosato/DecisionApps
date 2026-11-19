namespace DecisionApps.Contracts;

public static class SimulatorLimits
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxQuestions = 10_000;
    public const int MaxQuestionLength = 6_000;
    public const decimal ReviewThreshold = 0.60m;
}

public sealed record AgentCard(string Id, string Name, string Description);
public sealed record ImportedQuestion(
    int Id, string Sheet, int Row, string Question,
    string? Scope = null, string? ExpectedAgent = null, string? ScopeSource = null);
public sealed record ImportResult(string FileName, IReadOnlyList<ImportedQuestion> Questions);
public sealed record RatingRequest(string Question);
public sealed record OptionRating(string Id, string Name, decimal Probability, decimal Percentage);
public sealed record RoutingResult(
    string SelectedAgent,
    string SelectedAction,
    IReadOnlyList<OptionRating> Agents,
    IReadOnlyList<OptionRating> Actions,
    bool NeedsReview,
    long ElapsedMilliseconds,
    string Model,
    TokenUsage? Usage = null);

public sealed record TokenUsage(long InputTokens, long OutputTokens)
{
    public long TotalTokens => checked(InputTokens + OutputTokens);
}

public sealed record ModelConfiguration(string Model, string Endpoint, string Authentication);
