using RepoScout.Api.Analysis;

namespace RepoScout.Api.IntegrationTests;

public class GitHubRepoUrlTests
{
    [Theory]
    [InlineData("https://github.com/o/r", "o", "r")]
    [InlineData("http://github.com/o/r", "o", "r")]
    [InlineData("http://www.GitHub.com/o/r.git", "o", "r")]
    [InlineData("HTTPS://GITHUB.COM/o/r", "o", "r")]
    [InlineData("https://github.com/o/r/", "o", "r")]
    [InlineData("https://github.com/o/r.git/", "o", "r")]
    [InlineData("https://github.com/o/r/tree/main/x", "o", "r")]
    [InlineData("  https://github.com/o/r  ", "o", "r")]
    [InlineData("https://github.com/o/my.repo_name-1", "o", "my.repo_name-1")]
    public void TryParse_ValidUrl_ReturnsOwnerAndName(string input, string owner, string name)
    {
        var ok = GitHubRepoUrl.TryParse(input, out var repo);

        Assert.True(ok);
        Assert.Equal(new RepoRef(owner, name), repo);
    }

    [Fact]
    public void TryParse_Owner39Chars_Accepted()
    {
        var owner = new string('a', 39);

        var ok = GitHubRepoUrl.TryParse($"https://github.com/{owner}/r", out var repo);

        Assert.True(ok);
        Assert.Equal(owner, repo!.Owner);
    }

    [Theory]
    [InlineData("o/r")]
    [InlineData("github.com/o/r")]
    [InlineData("git@github.com:o/r.git")]
    [InlineData("https://github.com.evil.com/o/r")]
    [InlineData("https://evil.com/github.com/o/r")]
    [InlineData("https://gitlab.com/o/r")]
    [InlineData("ftp://github.com/o/r")]
    [InlineData("https://github.com/o")]
    [InlineData("https://github.com/")]
    [InlineData("https://github.com/o/..")]
    [InlineData("https://github.com/o/.")]
    [InlineData("https://github.com/o_x/r")]
    [InlineData("https://github.com/o/r%20x")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_InvalidInput_ReturnsFalse(string? input)
    {
        var ok = GitHubRepoUrl.TryParse(input, out var repo);

        Assert.False(ok);
        Assert.Null(repo);
    }

    [Fact]
    public void TryParse_Owner40Chars_ReturnsFalse()
    {
        var owner = new string('a', 40);

        var ok = GitHubRepoUrl.TryParse($"https://github.com/{owner}/r", out _);

        Assert.False(ok);
    }
}
