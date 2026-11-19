namespace Pong.Web.Decision;

public sealed class DecisionOptions
{
    public string Endpoint { get; set; } = "";
    public string Deployment { get; set; } = "";
    public string? ApiKey { get; set; }

    public static bool IsValid(DecisionOptions options) =>
        !string.IsNullOrWhiteSpace(options.Deployment) && options.Deployment.Length <= 200 &&
        Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.AbsolutePath.TrimEnd('/').EndsWith("/providers/microsoft/v1/systemone", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo);
}
