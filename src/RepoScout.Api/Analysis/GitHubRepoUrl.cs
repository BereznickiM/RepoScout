using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace RepoScout.Api.Analysis;

public static partial class GitHubRepoUrl
{
    public static bool TryParse(string? input, [NotNullWhen(true)] out RepoRef? repo)
    {
        repo = null;

        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        var owner = segments[0];
        var name = segments[1];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        if (name is "." or ".." || !OwnerRegex().IsMatch(owner) || !NameRegex().IsMatch(name))
        {
            return false;
        }

        repo = new RepoRef(owner, name);
        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9-]{1,39}$")]
    private static partial Regex OwnerRegex();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex NameRegex();
}
