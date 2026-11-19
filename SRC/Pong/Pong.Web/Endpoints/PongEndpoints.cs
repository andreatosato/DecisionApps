using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using Pong.Web.Agents;
using Pong.Web.Contracts;
using Pong.Web.Decision;
using Pong.Web.Game;

namespace Pong.Web.Endpoints;

public static class PongEndpoints
{
    public static void MapPongEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/configuration", (IOptions<DecisionOptions> options) => Results.Ok(new
        {
            deployment = options.Value.Deployment,
            authentication = string.IsNullOrWhiteSpace(options.Value.ApiKey) ? "Microsoft Entra ID" : "API key"
        }));
        routes.MapPost("/api/decisions", DecideAsync).RequireRateLimiting("inference");
    }

    private static async Task<IResult> DecideAsync(
        DecisionRequest request, AIAgent agent, HttpContext context,
        ILogger<PongDecisionAgent> logger, CancellationToken cancellationToken)
    {
        if (!StateValidator.IsValid(request))
        {
            return Results.Problem("Invalid Pong state: provide finite, bounded geometry, velocities, movement interval and chronological observations matching the current state.", statusCode: 400);
        }
        if (!StateValidator.CanDecide(request))
        {
            return Results.Problem("Decision acts only on a ball in the right half moving toward the right paddle.", statusCode: 400);
        }
        try
        {
            var response = await agent.RunAsync(JsonSerializer.Serialize(request), cancellationToken: cancellationToken);
            var result = JsonSerializer.Deserialize<DecisionResult>(response.Text, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new DecisionException("The agent returned no decision.");
            return Results.Ok(result);
        }
        catch (DecisionException ex)
        {
            logger.LogWarning("Decision inference failed ({StatusCode}): {Reason}", ex.StatusCode, ex.Message);
            if (ex.StatusCode == 429)
            {
                context.Response.Headers.RetryAfter = "1";
            }
            return Results.Problem(ex.Message, statusCode: ex.StatusCode);
        }
    }
}
