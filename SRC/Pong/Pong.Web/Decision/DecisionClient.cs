using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Pong.Web.Contracts;
using Pong.Web.Game;

namespace Pong.Web.Decision;

public sealed class DecisionClient(HttpClient http, DecisionTokenProvider tokens, IOptions<DecisionOptions> configuration)
{
    public async Task<DecisionResult> EvaluateAsync(
        DecisionRequest state, string instructions, IReadOnlyDictionary<string, string> criteria,
        CancellationToken cancellationToken)
    {
        if (!StateValidator.CanDecide(state))
        {
            throw new DecisionException("Decision acts only on a valid incoming ball in the right half.", 400);
        }
        var options = configuration.Value;
        var parentActivity = Activity.Current;
        parentActivity?.SetTag("gen_ai.provider.name", DecisionTelemetry.ProviderName);
        parentActivity?.SetTag("gen_ai.request.model", options.Deployment);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = options.Deployment,
                state,
                questions = new
                {
                    action = new
                    {
                        type = "choice",
                        instructions,
                        criteria
                    }
                }
            })
        };
        // SystemOne requires Content-Length, not chunked transfer encoding.
        await request.Content.LoadIntoBufferAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.Add("api-key", options.ApiKey);
        }
        else
        {
            try
            {
                var token = await tokens.GetTokenAsync(cancellationToken);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            }
            catch (Exception ex) when (ex is AuthenticationFailedException or CredentialUnavailableException)
            {
                parentActivity?.SetTag("error.type", "authentication_error");
                parentActivity?.SetStatus(ActivityStatusCode.Error);
                throw new DecisionException("Azure authentication is unavailable. Sign in locally or configure Decision:ApiKey in user-secrets.", 503, ex);
            }
        }

        var tags = DecisionTelemetry.CreateTags(options);
        using var activity = DecisionTelemetry.ModelSource.StartActivity(
            $"{DecisionTelemetry.OperationName} {options.Deployment}", ActivityKind.Client,
            parentContext: default, tags: tags);
        activity?.SetTag("decision.protocol", "systemone");
        var timer = Stopwatch.StartNew();
        TimeSpan? requestDuration = null;
        var failureRecorded = false;
        try
        {
            // ResponseContentRead buffers the complete response; parsing/auth are excluded from model latency.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            timer.Stop();
            requestDuration = timer.Elapsed;
            activity?.SetTag("http.response.status_code", (int)response.StatusCode);
            if (response.Headers.TryGetValues("apim-request-id", out var requestIds))
            {
                activity?.SetTag("decision.request.id", requestIds.FirstOrDefault());
            }
            if (!response.IsSuccessStatusCode)
            {
                MarkFailure(((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
                throw response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => new DecisionException("Decision is rate limited. Try again shortly.", 429),
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        new DecisionException("Decision rejected authentication or inference permissions.", 502),
                    HttpStatusCode.ServiceUnavailable => new DecisionException("Decision is unavailable.", 503),
                    HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout =>
                        new DecisionException("Decision timed out.", 504),
                    _ => new DecisionException($"Decision returned HTTP {(int)response.StatusCode}.")
                };
            }
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var result = DecisionResponseMapper.Map(payload.RootElement, state.Sequence, timer.ElapsedMilliseconds, options.Deployment);
            if (payload.RootElement.TryGetProperty("model", out var model) &&
                model.ValueKind == JsonValueKind.String && model.GetString() is { Length: > 0 and <= 256 } responseModel)
            {
                activity?.SetTag("gen_ai.response.model", responseModel);
                parentActivity?.SetTag("gen_ai.response.model", responseModel);
                tags.Add("gen_ai.response.model", responseModel);
            }
            DecisionTelemetry.RecordUsage(result.Usage, tags);
            if (result.Usage is { } usage)
            {
                activity?.SetTag("gen_ai.usage.input_tokens", usage.InputTokens);
                activity?.SetTag("gen_ai.usage.output_tokens", usage.OutputTokens);
            }
            activity?.SetTag("decision.action", result.Action);
            parentActivity?.SetTag("decision.action", result.Action);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag("decision.cancelled", true);
            parentActivity?.SetTag("decision.cancelled", true);
            tags.Add("decision.cancelled", true);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            MarkFailure("timeout");
            throw new DecisionException("Decision timed out.", 504, ex);
        }
        catch (HttpRequestException ex)
        {
            MarkFailure(ex.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded
                ? "response_too_large" : "connection_error");
            if (ex.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
            {
                throw new DecisionException("Decision returned an oversized response.", 502, ex);
            }
            throw new DecisionException("Unable to reach Decision.", 503, ex);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            MarkFailure("invalid_response");
            throw new DecisionException("Decision returned an invalid response.", inner: ex);
        }
        catch (DecisionException)
        {
            if (!failureRecorded)
            {
                MarkFailure("invalid_response");
            }
            throw;
        }
        finally
        {
            var duration = requestDuration ?? timer.Elapsed;
            activity?.SetTag("decision.model.duration_ms", duration.TotalMilliseconds);
            activity?.SetEndTime(activity.StartTimeUtc + duration);
            DecisionTelemetry.RecordDuration(duration.TotalSeconds, tags);
        }

        void MarkFailure(string errorType)
        {
            failureRecorded = true;
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("error.type", errorType);
            parentActivity?.SetTag("error.type", errorType);
            tags.Add("error.type", errorType);
        }
    }

}
