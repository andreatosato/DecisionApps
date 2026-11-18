namespace DecisionApps.Contracts;

public sealed class ExecutionUsage
{
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long TotalTokens => checked(InputTokens + OutputTokens);
    public int ReportedResponses { get; private set; }
    public int MissingUsageResponses { get; private set; }
    public long ModelElapsedMilliseconds { get; private set; }
    public int TimedResponses { get; private set; }

    public void AddModelDuration(long elapsedMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMilliseconds);
        ModelElapsedMilliseconds = checked(ModelElapsedMilliseconds + elapsedMilliseconds);
        TimedResponses++;
    }

    public void Add(TokenUsage? usage)
    {
        if (usage is null)
        {
            MissingUsageResponses++;
        }
        else
        {
            InputTokens = checked(InputTokens + usage.InputTokens);
            OutputTokens = checked(OutputTokens + usage.OutputTokens);
            ReportedResponses++;
        }
    }
}
