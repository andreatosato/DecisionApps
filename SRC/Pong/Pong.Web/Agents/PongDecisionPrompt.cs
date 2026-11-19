using System.Collections.ObjectModel;

namespace Pong.Web.Agents;

public static class PongDecisionPrompt
{
    public const string Instructions = """
        Control the right paddle in Pong. Choose up, down or stay to intercept the ball.
        All positions are center coordinates in pixels; y increases downwards.
        Velocities and paddle speed are pixels per second. The ball bounces at the
        top and bottom using its radius. Predict the future intercept with the
        paddle's left face from the full state, including repeated wall bounces.
        Account for ball direction, paddle dimensions, movement latency and the time
        until intercept. decisionIntervalSeconds is the nominal update cadence, not
        a fixed movement duration. An action can remain active until the next answer,
        but expires 350 milliseconds after its observation. You are invoked only
        while the ball is in the right half and moving toward the right paddle.
        Use velocityX and velocityY, not just the current ball y, to choose the most
        useful direction for the next decision interval. The game keeps moving during
        inference. Every request is a fresh observation, not a command to repeat the
        previous action. history contains up to five actual observations, oldest
        first, with browser-monotonic timestampMilliseconds, ballX, ballY, velocityX,
        velocityY and paddleY. observedAtMilliseconds identifies the current state.
        History can restart after a bounce or a new turn; never assume missing points.
        No local trajectory or interception estimate is supplied: infer the future
        interception yourself from the velocities, walls and observed history.
        estimatedResponseMilliseconds, when present, is smoothed measured round-trip
        latency, not a guarantee. Use it and the observed paddle motion to account
        for the ball and paddle continuing to move while you decide.
        Re-evaluate the future interception height and compare it with the paddle.
        Reverse direction when appropriate to avoid overshooting. Choose stay when
        the paddle already covers the anticipated ball path or further movement
        would overshoot. Consider the paddle's full height rather than chasing the
        ball center. Never output an action outside up, down and stay.
        """;

    public static IReadOnlyDictionary<string, string> Criteria { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
        {
            ["up"] = "Move the paddle upward (decrease paddle center y) for the next interval.",
            ["down"] = "Move the paddle downward (increase paddle center y) for the next interval.",
            ["stay"] = "Keep the paddle at its current position; do not move up or down."
        });
}
