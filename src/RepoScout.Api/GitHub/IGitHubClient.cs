using RepoScout.Api.Analysis;

namespace RepoScout.Api.GitHub;

public interface IGitHubClient
{
    Task<RepoInfo> GetRepoAsync(RepoRef repo, CancellationToken ct);

    Task<RepoTree> GetTreeAsync(RepoRef repo, string gitRef, CancellationToken ct);

    Task<RepoFile> GetFileAsync(RepoRef repo, string path, string gitRef, int maxBytes, CancellationToken ct);
}
