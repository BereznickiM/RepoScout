namespace RepoScout.Api.Analysis;

public interface IRepoAnalyzer
{
    /// <summary>Analyzes the repository and returns a Markdown report with usage statistics.</summary>
    Task<AnalysisResult> AnalyzeAsync(RepoRef repo, string question, CancellationToken ct);
}
