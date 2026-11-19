using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI;
using Pong.Web.Decision;

namespace Pong.Web.Agents;

public static class AgentRegistration
{
    public static IServiceCollection AddPongAgent(this IServiceCollection services)
    {
        services.AddOptions<DecisionOptions>().BindConfiguration("Decision")
            .Validate(DecisionOptions.IsValid, "Decision requires an HTTPS SystemOne endpoint and deployment.")
            .ValidateOnStart();
        services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DecisionTokenProvider>();
        services.AddHttpClient<DecisionClient>(http =>
        {
            http.Timeout = TimeSpan.FromSeconds(8);
            http.MaxResponseContentBufferSize = 64 * 1024;
        });
        services.AddTransient<PongDecisionAgent>();
        services.AddTransient<AIAgent>(provider => provider.GetRequiredService<PongDecisionAgent>()
            .AsBuilder()
            .UseOpenTelemetry(DecisionTelemetry.AgentSourceName, options => options.EnableSensitiveData = false)
            .Build());
        return services;
    }
}
