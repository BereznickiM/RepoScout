namespace RepoScout.Api.Ai;

public sealed class AiRateLimitException(IReadOnlyList<string> modelsTried)
    : Exception("All configured models are rate-limited.")
{
    // For logs only; never expose to API clients.
    public IReadOnlyList<string> ModelsTried { get; } = modelsTried;
}
