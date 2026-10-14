using System.Text;
using RepoScout.Api.GitHub;

namespace RepoScout.Api.Analysis;

public sealed class MockRepoAnalyzer(IGitHubClient github) : IRepoAnalyzer
{
    private const int MaxDepth = 2;

    public async Task<string> AnalyzeAsync(RepoRef repo, string question, CancellationToken ct)
    {
        var info = await github.GetRepoAsync(repo, ct);
        var tree = await github.GetTreeAsync(repo, info.DefaultBranch, ct);

        var report = new StringBuilder();
        report.Append("# Folder structure of ").Append(info.FullName).Append("\n\n");

        var directories = tree.Entries
            .Where(e => e.Kind == RepoTreeEntryKind.Directory)
            .Select(e => e.Path)
            .Where(p => p.Count(c => c == '/') < MaxDepth)
            .OrderBy(p => p.Replace('/', '\0'), StringComparer.Ordinal); // keeps children right after their parent

        foreach (var path in directories)
        {
            var depth = path.Count(c => c == '/');
            var name = path[(path.LastIndexOf('/') + 1)..];
            report.Append(' ', depth * 2).Append("- `").Append(name).Append("/`\n");
        }

        return report.ToString();
    }
}
