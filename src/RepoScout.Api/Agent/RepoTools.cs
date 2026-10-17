using System.Text;
using Microsoft.Extensions.AI;
using RepoScout.Api.Analysis;
using RepoScout.Api.GitHub;

namespace RepoScout.Api.Agent;

// Read-only tools over one repository, created per analysis.
public sealed class RepoTools
{
    private const int MaxResultBytes = 12_000;
    private const int MaxDepth = 2;
    private const int MaxListEntries = 300;

    private readonly IGitHubClient _github;
    private readonly RepoRef _repo;
    private readonly string _gitRef;
    private RepoTree? _tree;

    public RepoTools(IGitHubClient github, RepoRef repo, string gitRef)
    {
        _github = github;
        _repo = repo;
        _gitRef = gitRef;
        Functions =
        [
            AIFunctionFactory.Create(
                (string path = "", CancellationToken ct = default) => ListFilesAsync(path, ct), "list_files",
                "Lists files and directories under path (repo root if empty), up to 2 levels deep. Noise directories are hidden."),
            AIFunctionFactory.Create(
                (string path, CancellationToken ct = default) => ReadFileAsync(path, ct), "read_file",
                $"Returns the content of a file (first {MaxResultBytes:N0} bytes).")
        ];
    }

    public IList<AITool> Functions { get; }

    private async Task<string> ListFilesAsync(string path = "", CancellationToken ct = default)
    {
        path = (path ?? "").Trim().Trim('/');
        try
        {
            var tree = await GetTreeAsync(ct);
            if (path.Length > 0 && !tree.Entries.Any(e => e.Kind == RepoTreeEntryKind.Directory && e.Path == path))
            {
                return $"Error: no such directory: {path}";
            }

            var prefix = path.Length == 0 ? "" : path + "/";
            var entries = tree.Entries
                .Where(e => e.Path.StartsWith(prefix, StringComparison.Ordinal)
                    && e.Path.Count(c => c == '/') - prefix.Count(c => c == '/') < MaxDepth
                    && !NoiseFilter.IsNoise(e.Path))
                .OrderBy(e => e.Path.Replace('/', '\0'), StringComparer.Ordinal) // keeps children right after their parent
                .ToList();

            var text = new StringBuilder();
            foreach (var entry in entries.Take(MaxListEntries))
            {
                text.Append(entry.Path);
                if (entry.Kind == RepoTreeEntryKind.Directory)
                {
                    text.Append('/');
                }
                else if (entry.Size is { } size)
                {
                    text.Append(" (").Append(size).Append(" bytes)");
                }

                text.Append('\n');
            }

            if (entries.Count > MaxListEntries || tree.Truncated)
            {
                text.Append("(listing incomplete; list a subdirectory)\n");
            }

            return Wrap("list_files", path, text.ToString());
        }
        catch (Exception ex) when (ex is GitHubNotFoundException or ArgumentException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> ReadFileAsync(string path, CancellationToken ct = default)
    {
        path = (path ?? "").Trim().Trim('/');
        try
        {
            if (path.Length == 0)
            {
                return "Error: path is required";
            }

            if (NoiseFilter.IsNoise(path))
            {
                return "Error: skipped (generated/binary/lockfile)";
            }

            var tree = await GetTreeAsync(ct);
            var entry = tree.Entries.FirstOrDefault(e => e.Path == path);
            if (entry is null && !tree.Truncated)
            {
                return "Error: file not found";
            }

            if (entry is { Kind: RepoTreeEntryKind.Directory })
            {
                return $"Error: {path} is a directory; use list_files";
            }

            var file = await _github.GetFileAsync(_repo, path, _gitRef, MaxResultBytes, ct);
            if (file.Content.Contains('\0'))
            {
                return "Error: binary file";
            }

            var content = file.Content;
            if (file.Truncated)
            {
                content += $"\n[truncated at {MaxResultBytes} bytes]";
            }

            return Wrap("read_file", path, content);
        }
        catch (Exception ex) when (ex is GitHubNotFoundException or ArgumentException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private async Task<RepoTree> GetTreeAsync(CancellationToken ct)
        => _tree ??= await _github.GetTreeAsync(_repo, _gitRef, ct);

    private static string Wrap(string source, string path, string content)
        => $"<repo_data source=\"{source}\" path=\"{path}\">\n{UntrustedText.Clean(content)}\n</repo_data>";
}
