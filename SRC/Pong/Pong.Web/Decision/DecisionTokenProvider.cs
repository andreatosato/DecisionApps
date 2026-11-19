using Azure.Core;
using Azure.Identity;

namespace Pong.Web.Decision;

public sealed class DecisionTokenProvider(
    TokenCredential credential, TimeProvider clock, IHostApplicationLifetime lifetime)
{
    private readonly object sync = new();
    private AccessToken? cached;
    private Task<AccessToken>? refresh;

    public ValueTask<AccessToken> GetTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var now = clock.GetUtcNow();
            if (cached is { } token && token.ExpiresOn > now.AddMinutes(2) &&
                (token.RefreshOn is not { } refreshOn || refreshOn > now))
            {
                return ValueTask.FromResult(token);
            }
            if (refresh is null || refresh.IsCompleted)
            {
                refresh = RefreshAsync();
            }
            // A point or browser pause cancels its waiter, not shared credential acquisition.
            return new ValueTask<AccessToken>(refresh.WaitAsync(cancellationToken));
        }
    }

    private async Task<AccessToken> RefreshAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), timeout.Token);
            lock (sync)
            {
                cached = token;
            }
            return token;
        }
        catch (OperationCanceledException ex) when (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            throw new AuthenticationFailedException("Azure credential acquisition timed out.", ex);
        }
    }
}
