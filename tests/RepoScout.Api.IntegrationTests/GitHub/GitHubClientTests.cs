using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RepoScout.Api.Analysis;
using RepoScout.Api.GitHub;

namespace RepoScout.Api.IntegrationTests.GitHub;

public class GitHubClientTests
{
    private static readonly RepoRef Repo = new("o", "r");

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static (GitHubClient Client, StubHttpMessageHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHttpMessageHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return (new GitHubClient(http), handler);
    }

    // ---- GetRepoAsync ----

    [Fact]
    public async Task GetRepoAsync_ValidResponse_MapsSnakeCaseFields()
    {
        var (client, handler) = Create(_ => Json("""
            {"full_name":"o/r","description":"desc","default_branch":"develop","language":"C#","private":false}
            """));

        var info = await client.GetRepoAsync(Repo, CancellationToken.None);

        Assert.Equal(new RepoInfo("o/r", "desc", "develop", "C#"), info);
        Assert.Equal("https://api.github.com/repos/o/r", handler.Requests.Single().RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetRepoAsync_NullDescriptionAndLanguage_MapsToNull()
    {
        var (client, _) = Create(_ => Json("""
            {"full_name":"o/r","description":null,"default_branch":"main","language":null,"private":false}
            """));

        var info = await client.GetRepoAsync(Repo, CancellationToken.None);

        Assert.Null(info.Description);
        Assert.Null(info.Language);
    }

    [Fact]
    public async Task GetRepoAsync_PrivateRepo_ThrowsNotFound()
    {
        var (client, _) = Create(_ => Json("""
            {"full_name":"o/r","description":null,"default_branch":"main","language":null,"private":true}
            """));

        await Assert.ThrowsAsync<GitHubNotFoundException>(() => client.GetRepoAsync(Repo, CancellationToken.None));
    }

    [Fact]
    public async Task GetRepoAsync_Status404_ThrowsNotFound()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<GitHubNotFoundException>(() => client.GetRepoAsync(Repo, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task GetRepoAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var (client, _) = Create(_ => Json("{}"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetRepoAsync(Repo, cts.Token));
    }

    // ---- GetTreeAsync ----

    [Fact]
    public async Task GetTreeAsync_MixedTypes_MapsKindsAndSizesAndSkipsUnknown()
    {
        var (client, _) = Create(_ => Json("""
            {"truncated":false,"tree":[
              {"path":"a.txt","type":"blob","size":12},
              {"path":"src","type":"tree"},
              {"path":"vendor/lib","type":"commit"},
              {"path":"weird","type":"something-else"}
            ]}
            """));

        var tree = await client.GetTreeAsync(Repo, "main", CancellationToken.None);

        Assert.Equal(
            [
                new RepoTreeEntry("a.txt", RepoTreeEntryKind.File, 12),
                new RepoTreeEntry("src", RepoTreeEntryKind.Directory, null),
                new RepoTreeEntry("vendor/lib", RepoTreeEntryKind.Submodule, null)
            ],
            tree.Entries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetTreeAsync_TruncatedFlag_IsPassedThrough(bool truncated)
    {
        var (client, _) = Create(_ => Json($$"""{"truncated":{{truncated.ToString().ToLowerInvariant()}},"tree":[]}"""));

        var tree = await client.GetTreeAsync(Repo, "main", CancellationToken.None);

        Assert.Equal(truncated, tree.Truncated);
    }

    [Fact]
    public async Task GetTreeAsync_ValidRef_RequestsRecursiveTreeUrl()
    {
        var (client, handler) = Create(_ => Json("""{"truncated":false,"tree":[]}"""));

        await client.GetTreeAsync(Repo, "main", CancellationToken.None);

        Assert.Equal("https://api.github.com/repos/o/r/git/trees/main?recursive=1", handler.Requests.Single().RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetTreeAsync_RefWithSlashAndSpace_EscapedAsSingleSegment()
    {
        var (client, handler) = Create(_ => Json("""{"truncated":false,"tree":[]}"""));

        await client.GetTreeAsync(Repo, "feat/x y", CancellationToken.None);

        Assert.Contains("/git/trees/feat%2Fx%20y?recursive=1", handler.Requests.Single().RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task GetTreeAsync_InvalidRef_ThrowsArgumentExceptionWithoutRequest(string gitRef)
    {
        var (client, handler) = Create(_ => Json("{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetTreeAsync(Repo, gitRef, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    // ---- GetFileAsync ----

    [Fact]
    public async Task GetFileAsync_UnderCap_ReturnsFullContentNotTruncated()
    {
        var (client, _) = Create(_ => Bytes("hello"u8.ToArray()));

        var file = await client.GetFileAsync(Repo, "README.md", "main", 100, CancellationToken.None);

        Assert.Equal(new RepoFile("README.md", "hello", false), file);
    }

    [Fact]
    public async Task GetFileAsync_ExactlyMaxBytes_NotTruncated()
    {
        var (client, _) = Create(_ => Bytes("12345"u8.ToArray()));

        var file = await client.GetFileAsync(Repo, "f.txt", "main", 5, CancellationToken.None);

        Assert.Equal("12345", file.Content);
        Assert.False(file.Truncated);
    }

    [Fact]
    public async Task GetFileAsync_OverCap_ReturnsMaxBytesAndTruncated()
    {
        var (client, _) = Create(_ => Bytes("1234567890"u8.ToArray()));

        var file = await client.GetFileAsync(Repo, "f.txt", "main", 5, CancellationToken.None);

        Assert.Equal("12345", file.Content);
        Assert.True(file.Truncated);
    }

    [Fact]
    public async Task GetFileAsync_EmptyFile_ReturnsEmptyContentNotTruncated()
    {
        var (client, _) = Create(_ => Bytes([]));

        var file = await client.GetFileAsync(Repo, "empty.txt", "main", 5, CancellationToken.None);

        Assert.Equal("", file.Content);
        Assert.False(file.Truncated);
    }

    [Fact]
    public async Task GetFileAsync_Request_UsesOnlyRawAcceptHeader()
    {
        var (client, handler) = Create(_ => Bytes("x"u8.ToArray()));

        await client.GetFileAsync(Repo, "a.txt", "main", 10, CancellationToken.None);

        var accept = handler.Requests.Single().Headers.Accept;
        Assert.Equal("application/vnd.github.raw+json", Assert.Single(accept).ToString());
    }

    [Fact]
    public async Task GetFileAsync_PathWithSpecialChars_EscapesSegmentsAndSetsRef()
    {
        var (client, handler) = Create(_ => Bytes("x"u8.ToArray()));

        await client.GetFileAsync(Repo, "a b/c#d.md", "main", 10, CancellationToken.None);

        Assert.Equal("https://api.github.com/repos/o/r/contents/a%20b/c%23d.md?ref=main", handler.Requests.Single().RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetFileAsync_RefWithSlashAndSpace_EscapedAsSingleSegment()
    {
        var (client, handler) = Create(_ => Bytes("x"u8.ToArray()));

        await client.GetFileAsync(Repo, "a.txt", "feat/x y", 10, CancellationToken.None);

        Assert.EndsWith("?ref=feat%2Fx%20y", handler.Requests.Single().RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/x")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("../x")]
    [InlineData("a/..")]
    [InlineData("a/")]
    public async Task GetFileAsync_InvalidPath_ThrowsArgumentExceptionWithoutRequest(string path)
    {
        var (client, handler) = Create(_ => Bytes("x"u8.ToArray()));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetFileAsync(Repo, path, "main", 10, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task GetFileAsync_InvalidRef_ThrowsArgumentExceptionWithoutRequest(string gitRef)
    {
        var (client, handler) = Create(_ => Bytes("x"u8.ToArray()));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetFileAsync(Repo, "a.txt", gitRef, 10, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetFileAsync_Status404_ThrowsNotFound()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<GitHubNotFoundException>(() => client.GetFileAsync(Repo, "a.txt", "main", 10, CancellationToken.None));
    }

    [Fact]
    public async Task GetFileAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var (client, _) = Create(_ => Bytes("x"u8.ToArray()));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetFileAsync(Repo, "a.txt", "main", 10, cts.Token));
    }

    // ---- Error mapping ----

    [Fact]
    public async Task GetRepoAsync_403WithRemainingZeroAndReset_ThrowsRateLimitWithResetsAt()
    {
        var (client, _) = Create(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
            r.Headers.Add("x-ratelimit-remaining", "0");
            r.Headers.Add("x-ratelimit-reset", "1700000000");
            return r;
        });

        var ex = await Assert.ThrowsAsync<GitHubRateLimitException>(() => client.GetRepoAsync(Repo, CancellationToken.None));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), ex.ResetsAt);
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [Fact]
    public async Task GetRepoAsync_429WithRetryAfter_ThrowsRateLimitWithResetsAtAboutNowPlusDelay()
    {
        var (client, _) = Create(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return r;
        });

        var before = DateTimeOffset.UtcNow;
        var ex = await Assert.ThrowsAsync<GitHubRateLimitException>(() => client.GetRepoAsync(Repo, CancellationToken.None));
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(ex.ResetsAt);
        Assert.InRange(ex.ResetsAt.Value, before.AddSeconds(30), after.AddSeconds(30));
    }

    [Fact]
    public async Task GetRepoAsync_403RemainingZeroWithoutResetHeaders_ThrowsRateLimitWithNullResetsAt()
    {
        var (client, _) = Create(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
            r.Headers.Add("x-ratelimit-remaining", "0");
            return r;
        });

        var ex = await Assert.ThrowsAsync<GitHubRateLimitException>(() => client.GetRepoAsync(Repo, CancellationToken.None));

        Assert.Null(ex.ResetsAt);
    }

    [Fact]
    public async Task GetRepoAsync_Plain403_ThrowsBaseApiExceptionNotRateLimit()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetRepoAsync(Repo, CancellationToken.None));

        Assert.IsNotType<GitHubRateLimitException>(ex);
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [Fact]
    public async Task GetRepoAsync_403WithRemainingNonZero_ThrowsBaseApiExceptionNotRateLimit()
    {
        var (client, _) = Create(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
            r.Headers.Add("x-ratelimit-remaining", "42");
            return r;
        });

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetRepoAsync(Repo, CancellationToken.None));

        Assert.IsNotType<GitHubRateLimitException>(ex);
    }

    [Fact]
    public async Task GetRepoAsync_Status500_ThrowsApiExceptionWithStatusCode()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetRepoAsync(Repo, CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetRepoAsync_ErrorResponse_MessageDoesNotLeakBodyOrHeaders(HttpStatusCode status)
    {
        var (client, _) = Create(_ =>
        {
            var r = Json("""{"message":"BODY-SECRET-123"}""", status);
            r.Headers.Add("x-leak", "HEADER-SECRET-456");
            return r;
        });

        var ex = await Assert.ThrowsAnyAsync<GitHubApiException>(() => client.GetRepoAsync(Repo, CancellationToken.None));

        Assert.DoesNotContain("BODY-SECRET-123", ex.Message);
        Assert.DoesNotContain("HEADER-SECRET-456", ex.Message);
    }

    // ---- DI registration ----

    [Fact]
    public async Task AddGitHubClient_ResolvedClient_UsesBaseAddressHeadersAndNoAuthorization()
    {
        var handler = new StubHttpMessageHandler(_ => Json("""
            {"full_name":"o/r","description":null,"default_branch":"main","language":null,"private":false}
            """));
        var services = new ServiceCollection();
        services.AddGitHubClient().ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<GitHubClient>();
        await client.GetRepoAsync(Repo, CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal("https://api.github.com/repos/o/r", request.RequestUri!.AbsoluteUri);
        Assert.Contains(request.Headers.UserAgent, p => p.Product?.Name == "RepoScout");
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/vnd.github+json");
        Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Authorization"));
    }
}
