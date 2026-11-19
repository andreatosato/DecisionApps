using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;
using ClosedXML.Excel;
using DecisionApps.ApiService.Services;
using DecisionApps.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DecisionApps.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task RatesViaAgentFrameworkAndUsesSystemOneChoiceSchema()
    {
        using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest("Voglio aprire una segnalazione"));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RoutingResult>();
        Assert.NotNull(result);
        Assert.Equal("crm", result.SelectedAgent);
        Assert.Equal("Dispositiva", result.SelectedAction);
        Assert.Equal(100m, result.Agents.Sum(a => a.Percentage));
        Assert.Equal(1234, result.Usage!.InputTokens);
        Assert.Equal(2, result.Usage.OutputTokens);
        Assert.Equal(1236, result.Usage.TotalTokens);
        Assert.True(result.ElapsedMilliseconds >= 0);
        Assert.Equal(1, factory.Handler.Calls);
        Assert.NotNull(factory.Handler.RequestContentLength);
        Assert.Equal(Encoding.UTF8.GetByteCount(factory.Handler.RequestBody!), factory.Handler.RequestContentLength);
        using var body = JsonDocument.Parse(factory.Handler.RequestBody!);
        Assert.Equal("Decision-1.1", body.RootElement.GetProperty("model").GetString());
        var questions = body.RootElement.GetProperty("questions");
        Assert.Equal("choice", questions.GetProperty("agent").GetProperty("type").GetString());
        Assert.Equal(5, questions.GetProperty("agent").GetProperty("criteria").EnumerateObject().Count());
        Assert.Contains("Servizi scolastici", questions.GetProperty("agent").GetProperty("criteria").GetProperty("scuola").GetString());
        Assert.Contains("Capacit\u00e0 principali", questions.GetProperty("agent").GetProperty("criteria").GetProperty("informativo").GetString());
        foreach (var criterion in questions.GetProperty("agent").GetProperty("criteria").EnumerateObject())
        {
            Assert.DoesNotContain("\u00c3", criterion.Value.GetString()!);
        }
        Assert.Equal(2, questions.GetProperty("action").GetProperty("criteria").EnumerateObject().Count());
        Assert.EndsWith("/providers/microsoft/v1/systemone", factory.Handler.RequestUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectsEmptyQuestionWithoutCallingModel(string question)
    {
        using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest(question));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Handler.Calls);
    }

    [Fact]
    public async Task RejectsOversizedQuestionWithoutCallingModel()
    {
        using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest(new string('a', 6001)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Handler.Calls);
    }

    [Theory]
    [InlineData(401, 502)]
    [InlineData(403, 502)]
    [InlineData(404, 502)]
    [InlineData(429, 429)]
    [InlineData(500, 502)]
    public async Task SurfacesModelFailuresWithoutRetryOrFakeScores(int upstream, int expected)
    {
        using var factory = new TestApiFactory();
        factory.Handler.StatusCode = (HttpStatusCode)upstream;
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest("Una domanda"));
        Assert.Equal(expected, (int)response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("detail", text);
        Assert.DoesNotContain("private-upstream-body", text);
        Assert.DoesNotContain("\"agents\"", text);
        Assert.Equal(1, factory.Handler.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"answers\":{\"agent\":{\"type\":\"choice\",\"choice\":\"crm\",\"probabilities\":{}}}}")]
    public async Task RejectsMalformedModelResponse(string json)
    {
        using var factory = new TestApiFactory();
        factory.Handler.ResponseBody = json;
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest("Una domanda"));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, factory.Handler.Calls);
    }

    [Theory]
    [InlineData(true, 504)]
    [InlineData(false, 503)]
    public async Task SurfacesTimeoutAndNetworkErrors(bool timeout, int expected)
    {
        using var factory = new TestApiFactory();
        factory.Handler.Failure = timeout ? new TaskCanceledException() : new HttpRequestException();
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/ratings", new RatingRequest("Una domanda"));
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(1, factory.Handler.Calls);
    }

    [Fact]
    public async Task PropagatesCancellationToModelTransport()
    {
        using var factory = new TestApiFactory();
        factory.Handler.WaitUntilCancelled = true;
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = client.PostAsJsonAsync("/api/ratings", new RatingRequest("Una domanda"), cancellation.Token);
        await factory.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await factory.Handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, factory.Handler.Calls);
    }

    [Fact]
    public async Task ListsAllFiveSuppliedCards()
    {
        using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        var cards = await client.GetFromJsonAsync<AgentCard[]>("/api/agents");
        Assert.NotNull(cards);
        Assert.Equal(["informativo", "crm", "scuola", "anagrafe", "tva"], cards.Select(a => a.Id));
        Assert.All(cards, card => Assert.NotEmpty(card.Description));
        Assert.Equal(0, factory.Handler.Calls);
    }

    [Theory]
    [InlineData("informativo", "Capacit\u00e0 principali")]
    [InlineData("crm", "Capacit\u00e0 principali")]
    [InlineData("scuola", "iscrizioni n\u00e9 pagamenti")]
    [InlineData("anagrafe", "pu\u00f2 richiedere")]
    [InlineData("tva", "mobilit\u00e0 per Roma")]
    public async Task ServesCorrectItalianCharactersInAgentCards(string id, string expectedText)
    {
        using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        var cards = await client.GetFromJsonAsync<AgentCard[]>("/api/agents");
        var card = Assert.Single(cards!, card => card.Id == id);
        Assert.Contains(expectedText, card.Description);
        Assert.DoesNotContain("\u00c3", card.Description);
        Assert.DoesNotContain("\u00c2", card.Description);
        Assert.DoesNotContain("\u00e2\u20ac", card.Description);
        Assert.DoesNotContain("\ufffd", card.Description);
    }

    [Fact]
    public async Task ImportsExcelWithoutCallingModel()
    {
        using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Campione");
        sheet.Cell("B1").Value = "Domanda";
        sheet.Cell("B2").Value = "Come richiedo un certificato?";
        using var stream = ExcelQuestionImporterTests.Save(workbook);
        using var form = new MultipartFormDataContent();
        form.Add(new StreamContent(stream), "file", "domande.xlsx");
        var response = await client.PostAsync("/api/import", form);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ImportResult>();
        Assert.NotNull(result);
        Assert.Single(result.Questions);
        Assert.Equal("Campione", result.Questions[0].Sheet);
        Assert.Equal(2, result.Questions[0].Row);
        Assert.Equal(0, factory.Handler.Calls);
    }

    [Fact]
    public async Task ImportsReferenceThenComparesWithoutLeakingLabelToModel()
    {
        using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Campione");
        sheet.Cell("A1").Value = "Foglio origine";
        sheet.Cell("B1").Value = "Domanda";
        sheet.Cell("A2").Value = "Ambito ATAC";
        sheet.Cell("B2").Value = "Voglio aprire una segnalazione";
        using var stream = ExcelQuestionImporterTests.Save(workbook);
        using var form = new MultipartFormDataContent();
        form.Add(new StreamContent(stream), "file", "domande.xlsx");
        var importResponse = await client.PostAsync("/api/import", form);
        importResponse.EnsureSuccessStatusCode();
        var imported = await importResponse.Content.ReadFromJsonAsync<ImportResult>();
        var question = Assert.Single(imported!.Questions);
        Assert.Equal("Ambito ATAC", question.Scope);
        Assert.Equal("informativo", question.ExpectedAgent);
        Assert.Equal("Foglio origine", question.ScopeSource);
        Assert.Equal(0, factory.Handler.Calls);

        var ratingResponse = await client.PostAsJsonAsync("/api/ratings", new RatingRequest(question.Question));
        ratingResponse.EnsureSuccessStatusCode();
        var result = await ratingResponse.Content.ReadFromJsonAsync<RoutingResult>();
        var comparison = RoutingEvaluation.Compare(question, result);
        Assert.False(comparison.Matches);
        Assert.Equal("informativo", comparison.ExpectedAgent);
        Assert.Equal("crm", comparison.SelectedAgent);
        using var body = JsonDocument.Parse(factory.Handler.RequestBody!);
        var state = body.RootElement.GetProperty("state");
        Assert.Single(state.EnumerateObject());
        Assert.Equal(question.Question, state.GetProperty("domanda").GetString());
        Assert.DoesNotContain("Ambito ATAC", factory.Handler.RequestBody!);
        Assert.DoesNotContain("expectedAgent", factory.Handler.RequestBody!);
    }

    [Fact]
    public async Task RejectsCorruptUploadWithProblemDetails()
    {
        using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3]), "file", "broken.xlsx");
        var response = await client.PostAsync("/api/import", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("detail", await response.Content.ReadAsStringAsync());
    }

    internal sealed class TestApiFactory(Action<IServiceCollection>? configure = null) : WebApplicationFactory<Program>
    {
        public DecisionHandler Handler { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TokenCredential>();
                services.AddSingleton<TokenCredential, TestCredential>();
                services.AddHttpClient<DecisionClient>().ConfigurePrimaryHttpMessageHandler(() => Handler);
                configure?.Invoke(services);
            });
        }
    }

    private sealed class TestCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token-not-a-real-credential", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    internal sealed class DecisionHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = JsonSerializer.Serialize(DecisionResponseMapperTests.ValidPayload(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        public Func<HttpContent>? ResponseContentFactory { get; set; }
        public string? RequestBody { get; private set; }
        public string? RequestUri { get; private set; }
        public long? RequestContentLength { get; private set; }
        public Exception? Failure { get; set; }
        public bool WaitUntilCancelled { get; set; }
        public Action? OnSend { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult();
            OnSend?.Invoke();
            if (Failure is not null)
            {
                throw Failure;
            }
            if (WaitUntilCancelled)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Cancelled.TrySetResult();
                    throw;
                }
            }
            RequestContentLength = request.Content!.Headers.ContentLength;
            RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            RequestUri = request.RequestUri!.ToString();
            var response = new HttpResponseMessage(StatusCode)
            {
                Content = ResponseContentFactory?.Invoke() ??
                    new StringContent(StatusCode == HttpStatusCode.OK ? ResponseBody : "private-upstream-body", Encoding.UTF8, "application/json")
            };
            response.Headers.Add("apim-request-id", "test-model-request-id");
            return response;
        }
    }
}
