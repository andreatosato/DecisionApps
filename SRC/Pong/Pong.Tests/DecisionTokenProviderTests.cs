using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Hosting;
using Pong.Web.Decision;

namespace Pong.Tests;

public sealed class DecisionTokenProviderTests
{
    [Fact]
    public async Task ReusesTokenAcrossDecisionRequestsUntilRefreshMargin()
    {
        var clock = new Clock();
        var credential = new Credential(clock);
        var provider = new DecisionTokenProvider(credential, clock, new Lifetime());
        var first = await provider.GetTokenAsync(default);
        for (var i = 0; i < 10; i++) Assert.Equal(first, await provider.GetTokenAsync(default));
        Assert.Equal(1, credential.Calls);
        clock.Now = clock.Now.AddMinutes(59);
        await provider.GetTokenAsync(default);
        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public async Task HonoursProviderRefreshTimeBeforeExpiration()
    {
        var clock = new Clock();
        var credential = new Credential(clock) { RefreshAfter = TimeSpan.FromMinutes(10) };
        var provider = new DecisionTokenProvider(credential, clock, new Lifetime());
        await provider.GetTokenAsync(default);
        clock.Now = clock.Now.AddMinutes(11);
        await provider.GetTokenAsync(default);
        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public async Task CancelledRallyDoesNotCancelSharedAuthenticationOrStartItAgain()
    {
        var clock = new Clock();
        var credential = new Credential(clock) { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var provider = new DecisionTokenProvider(credential, clock, new Lifetime());
        using var cancellation = new CancellationTokenSource();
        var first = provider.GetTokenAsync(cancellation.Token).AsTask();
        var second = provider.GetTokenAsync(default).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(credential.AcquisitionToken.IsCancellationRequested);
        Assert.Equal(1, credential.Calls);
        credential.Gate.SetResult();
        await second;
        await provider.GetTokenAsync(default);
        Assert.Equal(1, credential.Calls);
    }

    [Fact]
    public async Task FailedAuthenticationIsNotCachedAsSuccess()
    {
        var clock = new Clock();
        var credential = new Credential(clock) { Failure = new AuthenticationFailedException("offline-failure") };
        var provider = new DecisionTokenProvider(credential, clock, new Lifetime());
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => provider.GetTokenAsync(default).AsTask());
        credential.Failure = null;
        await provider.GetTokenAsync(default);
        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public async Task HostShutdownCancelsCredentialAcquisition()
    {
        var clock = new Clock();
        var credential = new Credential(clock) { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var stopping = new CancellationTokenSource();
        var provider = new DecisionTokenProvider(credential, clock, new Lifetime(stopping.Token));
        var pending = provider.GetTokenAsync(default).AsTask();
        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Credential(Clock clock) : TokenCredential
    {
        public int Calls { get; private set; }
        public TaskCompletionSource? Gate { get; init; }
        public Exception? Failure { get; set; }
        public TimeSpan? RefreshAfter { get; init; }
        public CancellationToken AcquisitionToken { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override async ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            AcquisitionToken = cancellationToken;
            Assert.Equal(["https://cognitiveservices.azure.com/.default"], requestContext.Scopes);
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            if (Failure is { } failure) throw failure;
            return new AccessToken("offline-token", clock.Now.AddHours(1),
                RefreshAfter is { } after ? clock.Now.Add(after) : null);
        }
    }

    private sealed class Lifetime(CancellationToken stopping = default) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => stopping;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
}
