using Pong.Web.Contracts;

namespace Pong.Web.Game;

public static class StateValidator
{
    public static bool CanDecide(DecisionRequest? request) =>
        request is not null && IsValid(request) && request.Ball.VelocityX > 0 &&
        request.Ball.X >= request.Arena.Width / 2 &&
        request.Ball.X <= request.Paddle.X - request.Paddle.Width / 2 - request.Ball.Radius;

    public static bool IsValid(DecisionRequest? request)
    {
        if (request is not { Arena: { } arena, Ball: { } ball, Paddle: { } paddle } ||
            request.Sequence < 0 || request.PlayMode is not ("realtime" or "steps"))
        {
            return false;
        }
        return InRange(arena.Width, 100, 4096) && InRange(arena.Height, 100, 2160) &&
            InRange(ball.Radius, 1, Math.Min(arena.Width, arena.Height) / 4) &&
            InRange(ball.X, ball.Radius, arena.Width - ball.Radius) &&
            InRange(ball.Y, ball.Radius, arena.Height - ball.Radius) &&
            InRange(ball.VelocityX, -5000, 5000) && InRange(ball.VelocityY, -5000, 5000) &&
            InRange(paddle.Width, 1, arena.Width / 4) &&
            InRange(paddle.Height, 1, arena.Height / 2) &&
            InRange(paddle.X, paddle.Width / 2, arena.Width - paddle.Width / 2) &&
            InRange(paddle.Y, paddle.Height / 2, arena.Height - paddle.Height / 2) &&
            InRange(paddle.Speed, double.Epsilon, 5000) &&
            InRange(request.DecisionIntervalSeconds, 0.01, 2) &&
            IsValidHistory(request, arena, ball, paddle);
    }

    private static bool IsValidHistory(DecisionRequest request, Arena arena, Ball ball, Paddle paddle)
    {
        if (request.History is not { } history)
        {
            return request.ObservedAtMilliseconds is null && request.EstimatedResponseMilliseconds is null;
        }
        if (history.Count is < 1 or > 5 || request.ObservedAtMilliseconds is not { } observedAt ||
            !InRange(observedAt, 0, double.MaxValue) ||
            (request.EstimatedResponseMilliseconds is { } latency && !InRange(latency, 0, 10000)))
        {
            return false;
        }
        var previousTimestamp = double.NegativeInfinity;
        foreach (var point in history)
        {
            if (point is null ||
                !InRange(point.TimestampMilliseconds, Math.Max(0, observedAt - 1000), observedAt) ||
                point.TimestampMilliseconds <= previousTimestamp ||
                !InRange(point.BallX, ball.Radius, arena.Width - ball.Radius) ||
                !InRange(point.BallY, ball.Radius, arena.Height - ball.Radius) ||
                !InRange(point.VelocityX, -5000, 5000) || !InRange(point.VelocityY, -5000, 5000) ||
                !InRange(point.PaddleY, paddle.Height / 2, arena.Height - paddle.Height / 2))
            {
                return false;
            }
            previousTimestamp = point.TimestampMilliseconds;
        }
        var latest = history[^1];
        return latest.TimestampMilliseconds == observedAt && latest.BallX == ball.X && latest.BallY == ball.Y &&
            latest.VelocityX == ball.VelocityX && latest.VelocityY == ball.VelocityY && latest.PaddleY == paddle.Y;
    }

    private static bool InRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;
}
