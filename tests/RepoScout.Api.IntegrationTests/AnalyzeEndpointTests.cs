using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RepoScout.Api.Analysis;
using RepoScout.Api.GitHub;

namespace RepoScout.Api.IntegrationTests;

public class AnalyzeEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ValidUrl = "https://github.com/o/r";
    private const string CannedReport = "# Canned report\n\nUses `src/app`.";
    private readonly HttpClient _client;

    public AnalyzeEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("AiModel:ApiKey", "test-key");
                b.ConfigureTestServices(s =>
                {
                    s.AddSingleton<IGitHubClient>(new FakeGitHubClient());
                    s.AddSingleton<IChatClient>(new FakeChatClient());
                });
            })
            .CreateClient();
    }

    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, CannedReport)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeGitHubClient : IGitHubClient
    {
        public Task<RepoInfo> GetRepoAsync(RepoRef repo, CancellationToken ct) =>
            Task.FromResult(new RepoInfo("o/r", null, "main", null));

        public Task<RepoTree> GetTreeAsync(RepoRef repo, string gitRef, CancellationToken ct) =>
            Task.FromResult(new RepoTree(
                [
                    new RepoTreeEntry("src", RepoTreeEntryKind.Directory, null),
                    new RepoTreeEntry("src/app", RepoTreeEntryKind.Directory, null),
                    new RepoTreeEntry("src/app/deep", RepoTreeEntryKind.Directory, null),
                    new RepoTreeEntry("README.md", RepoTreeEntryKind.File, 10)
                ],
                false));

        public Task<RepoFile> GetFileAsync(RepoRef repo, string path, string gitRef, int maxBytes, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private Task<HttpResponseMessage> Post(object body) => _client.PostAsJsonAsync("/api/analyze", body);

    [Fact]
    public async Task PostAnalyze_ValidRequest_ReturnsReportFromChatClient()
    {
        var response = await Post(new { repoUrl = ValidUrl, question = "What stack is used?" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("report").GetString();
        Assert.Equal(CannedReport, report);
    }

    [Fact]
    public async Task PostAnalyze_ValidRequest_ReturnsUsageWithCamelCaseKeys()
    {
        var response = await Post(new { repoUrl = ValidUrl, question = "q" });

        var usage = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("usage");
        Assert.Equal(1, usage.GetProperty("rounds").GetInt32());
        Assert.Equal(0, usage.GetProperty("toolCalls").GetInt32());
        Assert.Equal(JsonValueKind.Object, usage.GetProperty("toolCallsByName").ValueKind);
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("inputTokens").ValueKind);
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("outputTokens").ValueKind);
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("totalTokens").ValueKind);
        Assert.True(usage.GetProperty("durationMs").GetInt64() >= 0);
        Assert.False(usage.GetProperty("budgetExhausted").GetBoolean());
    }

    [Fact]
    public async Task PostAnalyze_QuestionWithSurroundingWhitespace_IsTrimmedInReport()
    {
        var response = await Post(new { repoUrl = ValidUrl, question = "  hi  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostAnalyze_MultilineQuestion_QuotesEveryLine()
    {
        var response = await Post(new { repoUrl = ValidUrl, question = "line1\nline2" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostAnalyze_QuestionExactly500Chars_ReturnsOk()
    {
        var response = await Post(new { repoUrl = ValidUrl, question = new string('a', 500) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostAnalyze_QuestionOver500Chars_ReturnsBadRequestWithQuestionError()
    {
        var response = await Post(new { repoUrl = ValidUrl, question = new string('a', 501) });

        await AssertValidationError(response, "Question");
    }

    [Theory]
    [InlineData("o/r")]
    [InlineData("https://gitlab.com/o/r")]
    [InlineData("https://github.com/o")]
    public async Task PostAnalyze_InvalidRepoUrl_ReturnsBadRequestWithRepoUrlError(string url)
    {
        var response = await Post(new { repoUrl = url, question = "q" });

        await AssertValidationError(response, "RepoUrl");
    }

    [Fact]
    public async Task PostAnalyze_InvalidRepoUrl_MessageExplainsExpectedFormat()
    {
        var response = await Post(new { repoUrl = "nope", question = "q" });

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Contains("https://github.com/owner/repo", errors.GetProperty("RepoUrl")[0].GetString());
    }

    [Fact]
    public async Task PostAnalyze_MissingRepoUrl_ReturnsBadRequestWithRepoUrlError()
    {
        var response = await Post(new { question = "q" });

        await AssertValidationError(response, "RepoUrl");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PostAnalyze_EmptyOrWhitespaceQuestion_ReturnsBadRequestWithQuestionError(string question)
    {
        var response = await Post(new { repoUrl = ValidUrl, question });

        await AssertValidationError(response, "Question");
    }

    [Fact]
    public async Task PostAnalyze_MissingQuestion_ReturnsBadRequestWithQuestionError()
    {
        var response = await Post(new { repoUrl = ValidUrl });

        await AssertValidationError(response, "Question");
    }

    [Fact]
    public async Task PostAnalyze_MalformedJson_ReturnsBadRequest()
    {
        using var content = new StringContent("{not json", Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/analyze", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostAnalyze_NonJsonContentType_ReturnsUnsupportedMediaType()
    {
        using var content = new StringContent("repoUrl=x", Encoding.UTF8, "text/plain");

        var response = await _client.PostAsync("/api/analyze", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task GetAnalyze_ReturnsMethodNotAllowed()
    {
        var response = await _client.GetAsync("/api/analyze");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private static async Task AssertValidationError(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _),
            $"Expected errors.{field} in: {body}");
    }
}
