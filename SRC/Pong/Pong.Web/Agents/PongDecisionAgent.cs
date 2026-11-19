using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Pong.Web.Contracts;
using Pong.Web.Decision;
using Pong.Web.Game;

namespace Pong.Web.Agents;

public sealed class PongDecisionAgent(DecisionClient client) : AIAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true
    };
    public override string Name => "PongDecisionAgent";
    public override string Description => "Native Decision 1.1 SystemOne inference for up/down/stay Pong control with observed history.";

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new PongSession());

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Observations travel with each request; conversation persistence is not supported.");

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Observations travel with each request; conversation persistence is not supported.");

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        var input = messages.ToArray();
        if (input.Length != 1 || input[0].Role != ChatRole.User || input[0].Text.Length > 8192)
        {
            throw new ArgumentException("Expected one numeric Pong state.", nameof(messages));
        }
        var state = JsonSerializer.Deserialize<DecisionRequest>(input[0].Text, JsonOptions);
        if (!StateValidator.CanDecide(state))
        {
            throw new ArgumentException("Expected an incoming ball in the right half of the Pong court.", nameof(messages));
        }
        var result = await client.EvaluateAsync(
            state!, PongDecisionPrompt.Instructions, PongDecisionPrompt.Criteria, cancellationToken);
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(result, JsonOptions)))
        {
            Usage = result.Usage is not { } usage ? null : new UsageDetails
            {
                InputTokenCount = usage.InputTokens,
                OutputTokenCount = usage.OutputTokens,
                TotalTokenCount = usage.TotalTokens
            }
        };
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await RunCoreAsync(messages, session, options, cancellationToken);
        foreach (var update in response.ToAgentResponseUpdates())
        {
            yield return update;
        }
    }

    private sealed class PongSession : AgentSession;
}
