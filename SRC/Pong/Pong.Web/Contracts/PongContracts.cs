namespace Pong.Web.Contracts;

public sealed record DecisionRequest(
    long Sequence, Arena Arena, Ball Ball, Paddle Paddle, double DecisionIntervalSeconds,
    IReadOnlyList<Observation>? History = null, double? ObservedAtMilliseconds = null,
    double? EstimatedResponseMilliseconds = null);
public sealed record Arena(double Width, double Height);
public sealed record Ball(double X, double Y, double VelocityX, double VelocityY, double Radius);
public sealed record Paddle(double X, double Y, double Width, double Height, double Speed);
public sealed record Observation(
    double TimestampMilliseconds, double BallX, double BallY,
    double VelocityX, double VelocityY, double PaddleY);
public sealed record ActionProbabilities(decimal Up, decimal Down, decimal Stay);
public sealed record TokenUsage(long InputTokens, long OutputTokens, long TotalTokens);
public sealed record DecisionResult(
    long Sequence, string Action, ActionProbabilities Probabilities,
    long ElapsedMilliseconds, string Deployment, TokenUsage? Usage);
