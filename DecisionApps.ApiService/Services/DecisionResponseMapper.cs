using DecisionApps.Contracts;

namespace DecisionApps.ApiService.Services;

public static class DecisionResponseMapper
{
    public static RoutingResult Map(DecisionPayload payload, IReadOnlyList<AgentCard> cards, long elapsed, string deployment)
    {
        if (payload.Answers is null ||
            !payload.Answers.TryGetValue("agent", out var agent) ||
            !payload.Answers.TryGetValue("action", out var action))
        {
            throw new DecisionException("La risposta Decision non contiene le classificazioni agent e action.");
        }

        var agents = MapChoice(agent, cards.ToDictionary(a => a.Id, a => a.Name));
        var actions = MapChoice(action, new Dictionary<string, string>
        {
            ["Informativa"] = "Informativa",
            ["Dispositiva"] = "Dispositiva"
        });
        var needsReview = agents[0].Probability < SimulatorLimits.ReviewThreshold ||
            actions[0].Probability < SimulatorLimits.ReviewThreshold ||
            agents[0].Probability - agents[1].Probability < 0.10m;

        return new RoutingResult(agent.Choice!, action.Choice!, agents, actions, needsReview,
            elapsed, payload.Model ?? deployment, MapUsage(payload.Usage));
    }

    internal static TokenUsage? MapUsage(DecisionUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }
        if (usage.InputTokens is not { } input || usage.OutputTokens is not { } output ||
            input < 0 || output < 0 || input > long.MaxValue - output)
        {
            throw new DecisionException("Consumi token Decision non validi: valori mancanti, negativi o fuori intervallo.");
        }
        return new TokenUsage(input, output);
    }

    private static IReadOnlyList<OptionRating> MapChoice(ChoiceAnswer answer, Dictionary<string, string> expected)
    {
        if (answer.Type != "choice" || answer.Choice is null || !expected.ContainsKey(answer.Choice) ||
            answer.Probabilities is not { } probabilities ||
            probabilities.Count != expected.Count ||
            expected.Keys.Any(key => !probabilities.ContainsKey(key)) ||
            probabilities.Values.Any(p => p < 0 || p > 1))
        {
            throw new DecisionException("Classificazione Decision non valida: opzioni o probabilita mancanti/non ammesse.");
        }

        var sum = probabilities.Values.Sum();
        if (sum <= 0 || Math.Abs(sum - 1m) > 0.001m ||
            probabilities[answer.Choice] < probabilities.Values.Max())
        {
            throw new DecisionException("Probabilita Decision incoerenti: somma diversa da 1 o scelta non massima.");
        }

        var ordered = probabilities.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var exact = ordered.Select(p => p.Value / sum * 10_000m).ToArray();
        var basisPoints = exact.Select(p => (int)decimal.Floor(p)).ToArray();
        var remainder = 10_000 - basisPoints.Sum();
        // Largest remainder rounding keeps the displayed percentages at exactly 100.00%.
        foreach (var index in Enumerable.Range(0, ordered.Length)
            .OrderByDescending(i => exact[i] - basisPoints[i]).ThenBy(i => i).Take(remainder))
        {
            basisPoints[index]++;
        }

        return ordered.Select((p, i) =>
            new OptionRating(p.Key, expected[p.Key], p.Value / sum, basisPoints[i] / 100m)).ToArray();
    }
}
