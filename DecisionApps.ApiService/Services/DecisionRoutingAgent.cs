using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DecisionApps.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DecisionApps.ApiService.Services;

public sealed class DecisionRoutingAgent(
    DecisionClient client, IOptions<DecisionOptions> configuration) : AIAgent
{
    public override string Name => "DecisionRoutingAgent";
    public override string Description => "Classificazione esclusiva di agente e azione con Microsoft-Decision-1.1.";

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new DecisionSession());

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Ogni domanda usa una sessione indipendente senza cronologia.");

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Il simulatore non persiste sessioni o domande.");

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        var input = messages.ToArray();
        if (input.Length != 1 || input[0].Role != ChatRole.User ||
            string.IsNullOrWhiteSpace(input[0].Text) || input[0].Text.Length > SimulatorLimits.MaxQuestionLength)
        {
            throw new ArgumentException("Inviare una sola domanda utente valida.", nameof(messages));
        }

        var activity = Activity.Current;
        activity?.SetTag("gen_ai.provider.name", DecisionTelemetry.ProviderName);
        activity?.SetTag("gen_ai.request.model", configuration.Value.Deployment);
        try
        {
            var result = await client.EvaluateAsync(input[0].Text.Trim(), cancellationToken);
            activity?.SetTag("decision.agent", result.SelectedAgent);
            activity?.SetTag("decision.action", result.SelectedAction);
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(result)))
            {
                Usage = result.Usage is not { } usage ? null : new UsageDetails
                {
                    InputTokenCount = usage.InputTokens,
                    OutputTokenCount = usage.OutputTokens,
                    TotalTokenCount = usage.TotalTokens
                }
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag("decision.cancelled", true);
            throw;
        }
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

    private sealed class DecisionSession : AgentSession;
}
