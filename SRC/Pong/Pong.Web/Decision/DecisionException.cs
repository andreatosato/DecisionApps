namespace Pong.Web.Decision;

public sealed class DecisionException(string message, int statusCode = 502, Exception? inner = null)
    : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
}
