using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Pong.Web.Agents;
using Pong.Web.Endpoints;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
// Keep binding failures as 400 responses even when Development enables throwing by default.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.AddPongAgent();
builder.Services.AddRateLimiter(options =>
{
    options.AddConcurrencyLimiter("inference", limiter =>
    {
        limiter.PermitLimit = 2;
        limiter.QueueLimit = 0;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "1";
        await Results.Problem("Inference is busy. Try again shortly.", statusCode: 429)
            .ExecuteAsync(context.HttpContext);
    };
});
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 8192);

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/decisions") && context.Request.ContentLength > 8192)
    {
        await Results.Problem("The decision request exceeds 8 KiB.", statusCode: 413).ExecuteAsync(context);
        return;
    }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.MapPongEndpoints();
app.MapDefaultEndpoints();
app.Run();

public partial class Program;
