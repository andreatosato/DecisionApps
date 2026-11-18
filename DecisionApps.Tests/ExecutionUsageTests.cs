using DecisionApps.Contracts;

namespace DecisionApps.Tests;

public sealed class ExecutionUsageTests
{
    [Fact]
    public void TotalsOnlyReportedModelRequestDurations()
    {
        var usage = new ExecutionUsage();
        Assert.Equal(0, usage.TimedResponses);
        usage.AddModelDuration(250);
        usage.AddModelDuration(500);
        Assert.Equal(750, usage.ModelElapsedMilliseconds);
        Assert.Equal(2, usage.TimedResponses);
    }

    [Fact]
    public void DistinguishesZeroModelDurationFromNoMeasurement()
    {
        var usage = new ExecutionUsage();
        usage.AddModelDuration(0);
        Assert.Equal(0, usage.ModelElapsedMilliseconds);
        Assert.Equal(1, usage.TimedResponses);
        Assert.Equal(0, new ExecutionUsage().TimedResponses);
    }

    [Fact]
    public void RejectsNegativeModelDurations()
    {
        var usage = new ExecutionUsage();
        Assert.Throws<ArgumentOutOfRangeException>(() => usage.AddModelDuration(-1));
        Assert.Equal(0, usage.TimedResponses);
    }

    [Fact]
    public void AggregatesEachActualResponseIncludingReruns()
    {
        var usage = new ExecutionUsage();
        var response = new TokenUsage(1200, 2);
        usage.Add(response);
        usage.Add(response);
        usage.Add(new TokenUsage(600, 1));
        Assert.Equal(3000, usage.InputTokens);
        Assert.Equal(5, usage.OutputTokens);
        Assert.Equal(3005, usage.TotalTokens);
        Assert.Equal(3, usage.ReportedResponses);
        Assert.Equal(0, usage.MissingUsageResponses);
    }

    [Fact]
    public void MarksPartialTotalsExplicitly()
    {
        var usage = new ExecutionUsage();
        usage.Add(new TokenUsage(100, 2));
        usage.Add(null);
        Assert.Equal(102, usage.TotalTokens);
        Assert.Equal(1, usage.ReportedResponses);
        Assert.Equal(1, usage.MissingUsageResponses);
    }

    [Fact]
    public void DistinguishesUnreportedFromReportedZero()
    {
        var usage = new ExecutionUsage();
        usage.Add(null);
        Assert.Equal(0, usage.ReportedResponses);
        Assert.Equal(1, usage.MissingUsageResponses);
        usage.Add(new TokenUsage(0, 0));
        Assert.Equal(1, usage.ReportedResponses);
        Assert.Equal(0, usage.TotalTokens);
    }

    [Fact]
    public void NewExecutionStartsWithIndependentCounters()
    {
        var previous = new ExecutionUsage();
        previous.Add(new TokenUsage(100, 2));
        var current = new ExecutionUsage();
        Assert.Equal(0, current.ReportedResponses);
        Assert.Equal(0, current.MissingUsageResponses);
        Assert.Equal(0, current.TotalTokens);
        Assert.Equal(102, previous.TotalTokens);
    }
}
