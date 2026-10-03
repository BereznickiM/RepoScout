namespace RepoScout.Api.Analysis;

public interface IRepoAnalyzer
{
    /// <summary>Analyzes the repository and returns a Markdown report.</summary>
    Task<string> AnalyzeAsync(RepoRef repo, string question, CancellationToken ct);
}
