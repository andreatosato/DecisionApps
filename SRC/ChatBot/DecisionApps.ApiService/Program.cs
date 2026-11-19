using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using DecisionApps.ApiService.Services;
using DecisionApps.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<GenAiTelemetryOptions>().BindConfiguration("Telemetry");
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(DecisionTelemetry.AgentSourceName, DecisionTelemetry.ModelSourceName))
    .WithMetrics(metrics => metrics.AddMeter(DecisionTelemetry.AgentSourceName, DecisionTelemetry.ModelSourceName));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddOptions<DecisionOptions>()
    .BindConfiguration("Decision")
    .Validate(DecisionOptions.IsValid, "Configurare Decision:Endpoint HTTPS systemone e Decision:Deployment.")
    .ValidateOnStart();
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
builder.Services.AddSingleton<AgentCatalog>();
builder.Services.AddSingleton<ExcelQuestionImporter>();
builder.Services.AddHttpClient<DecisionClient>(http => http.Timeout = TimeSpan.FromSeconds(90));
builder.Services.AddTransient<DecisionRoutingAgent>();
builder.Services.AddTransient<AIAgent>(services =>
    services.GetRequiredService<DecisionRoutingAgent>()
        .AsBuilder()
        .UseOpenTelemetry(DecisionTelemetry.AgentSourceName, telemetry =>
            telemetry.EnableSensitiveData = services.GetRequiredService<IOptions<GenAiTelemetryOptions>>().Value.EnableSensitiveData)
        .Build());
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = SimulatorLimits.MaxFileBytes + 1024 * 1024);

var app = builder.Build();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/api/agents", (AgentCatalog catalog) => catalog.Agents);
app.MapGet("/api/configuration", (IOptions<DecisionOptions> options) =>
    new ModelConfiguration(options.Value.Deployment, options.Value.Endpoint,
        string.IsNullOrWhiteSpace(options.Value.ApiKey) ? "Microsoft Entra ID" : "API key"));

app.MapPost("/api/import", async (HttpRequest request, ExcelQuestionImporter importer,
    ILogger<ExcelQuestionImporter> logger, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.Problem("Inviare un file con multipart/form-data.", statusCode: 400);
    }

    try
    {
        var form = await request.ReadFormAsync(cancellationToken);
        if (form.Files.Count != 1 || form.Files[0].Length == 0)
        {
            return Results.Problem("Inviare esattamente un file Excel non vuoto.", statusCode: 400);
        }
        var file = form.Files[0];
        if (file.Length > SimulatorLimits.MaxFileBytes)
        {
            return Results.Problem("Il file supera il limite di 10 MB.", statusCode: 413);
        }
        await using var memory = new MemoryStream();
        await file.CopyToAsync(memory, cancellationToken);
        memory.Position = 0;
        return Results.Ok(importer.Import(memory, file.FileName, cancellationToken));
    }
    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FormatException or IOException)
    {
        logger.LogWarning("Importazione Excel rifiutata ({ErrorType}).", ex.GetType().Name);
        return Results.Problem(ex is InvalidDataException ? ex.Message : "File Excel non valido o danneggiato.", statusCode: 400);
    }
});

app.MapPost("/api/ratings", async (RatingRequest request, AIAgent agent,
    ILogger<DecisionRoutingAgent> logger, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > SimulatorLimits.MaxQuestionLength)
    {
        return Results.Problem("La domanda deve contenere da 1 a 6000 caratteri.", statusCode: 400);
    }
    try
    {
        var response = await agent.RunAsync(request.Question, cancellationToken: cancellationToken);
        var result = JsonSerializer.Deserialize<RoutingResult>(response.Text)
            ?? throw new DecisionException("Risposta dell'agente vuota.");
        return Results.Ok(result);
    }
    catch (DecisionException ex)
    {
        logger.LogWarning("Classificazione Decision fallita: {Reason}", ex.Message);
        return Results.Problem(ex.Message, statusCode: ex.StatusCode,
            extensions: ex.ModelElapsedMilliseconds is { } elapsed
                ? new Dictionary<string, object?> { ["modelElapsedMilliseconds"] = elapsed }
                : null);
    }
});

app.MapDefaultEndpoints();
app.Run();

public partial class Program;
