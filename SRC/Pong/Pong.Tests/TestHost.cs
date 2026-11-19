using System.Net;
using System.Text;
using Azure.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pong.Web.Contracts;
using Pong.Web.Decision;

namespace Pong.Tests;

internal sealed class TestHost : WebApplicationFactory<Program>
{
    public FakeHandler Handler { get; } = new();
    public FakeCredential Credential { get; } = new();
    public bool UseApiKey { get; init; }
    public string EnvironmentName { get; init; } = "Testing";
    public string Endpoint { get; init; } = "https://offline.invalid/providers/microsoft/v1/systemone";
    public Action<IServiceCollection>? CustomizeServices { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Decision:Endpoint"] = Endpoint,
                ["Decision:Deployment"] = "Decision-1.1",
                ["Decision:ApiKey"] = UseApiKey ? "offline-test-key" : "",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = ""
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TokenCredential>();
            services.AddSingleton<TokenCredential>(Credential);
            services.AddHttpClient<DecisionClient>().ConfigurePrimaryHttpMessageHandler(() => Handler);
            CustomizeServices?.Invoke(services);
        });
    }

    public static DecisionRequest State(long sequence = 5) =>
        new(sequence, new(960, 600), new(480, 200, 320, -150, 8), new(926, 300, 14, 88, 300), 0.35);

    public static DecisionRequest ObservedState() => State() with
    {
        ObservedAtMilliseconds = 200,
        EstimatedResponseMilliseconds = 145,
        History = [new(150, 464, 207.5, 320, -150, 300), new(200, 480, 200, 320, -150, 300)]
    };
}

internal sealed class FakeCredential : TokenCredential
{
    public int Calls;
    public string[]? Scopes { get; private set; }
    public Exception? Failure { get; set; }
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Async authentication only.");

    public override ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Calls);
        Scopes = requestContext.Scopes;
        if (Failure is { } failure)
        {
            throw failure;
        }
        return ValueTask.FromResult(new AccessToken("offline-token", DateTimeOffset.UtcNow.AddHours(1)));
    }
}

internal sealed class FakeHandler : HttpMessageHandler
{
    public const string ValidResponse = """
        {"answers":{"action":{"type":"choice","choice":"up","probabilities":{"up":0.8,"down":0.2,"stay":0}}},
         "usage":{"input_tokens":123,"output_tokens":2,"total_tokens":125}}
        """;
    public int Calls;
    public string ResponseBody { get; set; } = ValidResponse;
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
    public string? RequestBody { get; private set; }
    public string? RequestUri { get; private set; }
    public string? Authorization { get; private set; }
    public string? ApiKey { get; private set; }
    public long? ContentLength { get; private set; }
    public Exception? Failure { get; set; }
    public bool WaitUntilCancelled { get; set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref Calls);
        RequestUri = request.RequestUri!.ToString();
        RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        ContentLength = request.Content.Headers.ContentLength;
        Authorization = request.Headers.Authorization?.ToString();
        ApiKey = request.Headers.TryGetValues("api-key", out var keys) ? keys.Single() : null;
        Started.TrySetResult();
        if (call >= 2)
        {
            SecondStarted.TrySetResult();
        }
        if (Failure is { } failure)
        {
            throw failure;
        }
        if (WaitUntilCancelled)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
        return new HttpResponseMessage(StatusCode)
        {
            Headers = { { "apim-request-id", "offline-request-id" } },
            Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
        };
    }
}
