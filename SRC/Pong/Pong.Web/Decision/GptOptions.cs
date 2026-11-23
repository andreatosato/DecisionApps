namespace Pong.Web.Decision;

public sealed class GptOptions
{
    public string Endpoint { get; set; } = "";
    public string Deployment { get; set; } = "";
    public string? ApiKey { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Deployment);

    public static bool IsValid(GptOptions options) =>
        !options.IsConfigured ||
        (options.Deployment.Length <= 200 &&
         Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) &&
         uri.Scheme == Uri.UriSchemeHttps &&
         uri.AbsolutePath.TrimEnd('/') == "/openai/v1/chat/completions" &&
         string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo));
}
