using System.Net;

namespace RepoScout.Api.Ai;

public sealed class GeminiApiException(HttpStatusCode? status, string message) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = status;
}
