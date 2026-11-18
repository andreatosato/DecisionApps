extern alias WebFrontend;

using System.Net;
using System.Text;
using System.Text.Json;
using WebFrontend::DecisionApps.Web;

namespace DecisionApps.Tests;

public sealed class SimulatorApiClientTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    public async Task PreservesModelDurationFromProblemResponse(long duration)
    {
        using var http = CreateClient($$"""{"detail":"Rate limited","modelElapsedMilliseconds":{{duration}}}""");
        var client = new SimulatorApiClient(http);
        var exception = await Assert.ThrowsAsync<SimulatorApiException>(() => client.RateAsync("Una domanda", default));
        Assert.Equal(429, exception.StatusCode);
        Assert.Equal(duration, exception.ModelElapsedMilliseconds);
    }

    [Fact]
    public async Task DoesNotInventDurationWhenModelWasNotCalled()
    {
        using var http = CreateClient("""{"detail":"Authentication unavailable"}""");
        var exception = await Assert.ThrowsAsync<SimulatorApiException>(() =>
            new SimulatorApiClient(http).RateAsync("Una domanda", default));
        Assert.Null(exception.ModelElapsedMilliseconds);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("\"invalid\"")]
    public async Task RejectsInvalidDurationRatherThanDisplayingIt(string duration)
    {
        using var http = CreateClient($$"""{"detail":"Failure","modelElapsedMilliseconds":{{duration}}}""");
        await Assert.ThrowsAsync<JsonException>(() => new SimulatorApiClient(http).RateAsync("Una domanda", default));
    }

    private static HttpClient CreateClient(string body) =>
        new(new ProblemHandler(body)) { BaseAddress = new Uri("http://localhost") };

    private sealed class ProblemHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/problem+json")
            });
    }
}
