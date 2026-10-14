using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RepoScout.Api.Analysis;

namespace RepoScout.Api.GitHub;

public sealed class GitHubClient(HttpClient http) : IGitHubClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<RepoInfo> GetRepoAsync(RepoRef repo, CancellationToken ct)
    {
        var url = RepoUrl(repo);
        using var response = await http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, url);

        var dto = await response.Content.ReadFromJsonAsync<RepoDto>(JsonOptions, ct)
            ?? throw new GitHubApiException(response.StatusCode, $"Empty response from {url}");
        if (dto.Private)
        {
            throw new GitHubNotFoundException($"GitHub returned 404 for {url}");
        }

        return new RepoInfo(dto.FullName, dto.Description, dto.DefaultBranch, dto.Language);
    }

    public async Task<RepoTree> GetTreeAsync(RepoRef repo, string gitRef, CancellationToken ct)
    {
        var url = $"{RepoUrl(repo)}/git/trees/{EscapeRef(gitRef)}?recursive=1";
        using var response = await http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, url);

        var dto = await response.Content.ReadFromJsonAsync<TreeDto>(JsonOptions, ct)
            ?? throw new GitHubApiException(response.StatusCode, $"Empty response from {url}");
        var entries = (dto.Tree ?? [])
            .Select(i => i.Type switch
            {
                "blob" => new RepoTreeEntry(i.Path, RepoTreeEntryKind.File, i.Size),
                "tree" => new RepoTreeEntry(i.Path, RepoTreeEntryKind.Directory, null),
                "commit" => new RepoTreeEntry(i.Path, RepoTreeEntryKind.Submodule, null),
                _ => null
            })
            .OfType<RepoTreeEntry>()
            .ToList();
        return new RepoTree(entries, dto.Truncated);
    }

    public async Task<RepoFile> GetFileAsync(RepoRef repo, string path, string gitRef, int maxBytes, CancellationToken ct)
    {
        var url = $"{RepoUrl(repo)}/contents/{EscapePath(path)}?ref={EscapeRef(gitRef)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, url);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[maxBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        var truncated = total > maxBytes;
        return new RepoFile(path, Encoding.UTF8.GetString(buffer, 0, Math.Min(total, maxBytes)), truncated);
    }

    private static string RepoUrl(RepoRef repo) =>
        $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}";

    private static string EscapePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Contains('\\'))
        {
            throw new ArgumentException("Invalid path.", nameof(path));
        }

        var segments = path.Split('/');
        if (segments.Any(s => s is "" or "." or ".."))
        {
            throw new ArgumentException("Invalid path.", nameof(path));
        }

        return string.Join('/', segments.Select(Uri.EscapeDataString));
    }

    private static string EscapeRef(string gitRef)
    {
        if (string.IsNullOrEmpty(gitRef) || gitRef is "." or "..")
        {
            throw new ArgumentException("Invalid ref.", nameof(gitRef));
        }

        return Uri.EscapeDataString(gitRef);
    }

    private static Task EnsureSuccessAsync(HttpResponseMessage response, string url)
    {
        if (response.IsSuccessStatusCode)
        {
            return Task.CompletedTask;
        }

        var status = response.StatusCode;
        var message = $"GitHub returned {(int)status} for {url.Split('?')[0]}";

        if (status == HttpStatusCode.NotFound)
        {
            throw new GitHubNotFoundException(message);
        }

        if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var exhausted = response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining)
                && remaining.FirstOrDefault() == "0";
            var retryAfter = response.Headers.RetryAfter;
            if (exhausted || retryAfter is not null)
            {
                DateTimeOffset? resetsAt = null;
                if (response.Headers.TryGetValues("x-ratelimit-reset", out var reset)
                    && long.TryParse(reset.FirstOrDefault(), out var unix))
                {
                    resetsAt = DateTimeOffset.FromUnixTimeSeconds(unix);
                }
                else if (retryAfter?.Delta is { } delta)
                {
                    resetsAt = DateTimeOffset.UtcNow + delta;
                }
                else if (retryAfter?.Date is { } date)
                {
                    resetsAt = date;
                }

                throw new GitHubRateLimitException(status, message, resetsAt);
            }
        }

        throw new GitHubApiException(status, message);
    }

    private sealed record RepoDto(string FullName, string? Description, string DefaultBranch, string? Language, bool Private);

    private sealed record TreeDto(List<TreeItemDto>? Tree, bool Truncated);

    private sealed record TreeItemDto(string Path, string Type, long? Size);
}
