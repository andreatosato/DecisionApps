namespace DecisionApps.ApiService.Services;

public sealed class DecisionOptions
{
    public string Endpoint { get; set; } = "";
    public string Deployment { get; set; } = "";
    public string? ApiKey { get; set; }

    public static bool IsValid(DecisionOptions options) =>
        !string.IsNullOrWhiteSpace(options.Deployment) &&
        Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.AbsolutePath.TrimEnd('/').EndsWith("/providers/microsoft/v1/systemone", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo);
}

public sealed class DecisionException(
    string message, int statusCode = 502, Exception? inner = null, string errorType = "invalid_response")
    : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
    public string ErrorType { get; } = errorType;
    public long? ModelElapsedMilliseconds { get; internal set; }
}
