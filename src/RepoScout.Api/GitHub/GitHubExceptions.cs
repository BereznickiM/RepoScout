using System.Net;

namespace RepoScout.Api.GitHub;

public class GitHubApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = status;
}

public sealed class GitHubNotFoundException(string message) : GitHubApiException(HttpStatusCode.NotFound, message);

public sealed class GitHubRateLimitException(HttpStatusCode status, string message, DateTimeOffset? resetsAt)
    : GitHubApiException(status, message)
{
    public DateTimeOffset? ResetsAt { get; } = resetsAt;
}
