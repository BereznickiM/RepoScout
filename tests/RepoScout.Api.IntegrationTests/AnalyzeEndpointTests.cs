using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RepoScout.Api.Ai;
using RepoScout.Api.Analysis;
using RepoScout.Api.Controllers;
using RepoScout.Api.GitHub;

namespace RepoScout.Api.IntegrationTests;

public class AnalyzeEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ValidUrl = "https://github.com/o/r";
    private const string CannedReport = "# Canned report\n\nUses `src/app`.";
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;

    public AnalyzeEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
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
                    new RepoTreeEntry("README.md", RepoTreeEntryKind.File, 10)
                ],
                false));

        public Task<RepoFile> GetFileAsync(RepoRef repo, string path, string gitRef, int maxBytes, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingChatClient(Exception exception) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw exception;

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private Task<HttpResponseMessage> PostWithThrowingClient(Exception exception) =>
        PostWithChatClient(new ThrowingChatClient(exception));

    [Fact]
    public async Task PostAnalyze_AllModelsRateLimited_ReturnsTooManyRequestsLimitReachedWithoutModelNames()
    {
        var response = await PostWithThrowingClient(new AiRateLimitException(["secret-model-a", "secret-model-b"]));

        await AssertProblem(response, HttpStatusCode.TooManyRequests, "Limit reached",
            "The AI model's usage limit was reached. Please try again later.");
    }

    [Fact]
    public async Task PostAnalyze_ProviderOverloaded_ReturnsServiceUnavailableWithoutLeaks()
    {
        var response = await PostWithThrowingClient(
            new GeminiApiException(HttpStatusCode.ServiceUnavailable, "Gemini returned 503"));

        await AssertProblem(response, HttpStatusCode.ServiceUnavailable, "AI model provider unavailable",
            "The AI model provider is unavailable or overloaded, please try again in a few minutes.");
    }

    private static async Task AssertProblem(
        HttpResponseMessage response, HttpStatusCode status, string title, string? detail = null)
    {
        Assert.Equal(status, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.Equal(title, body.GetProperty("title").GetString());
        if (detail is not null)
        {
            Assert.Equal(detail, body.GetProperty("detail").GetString());
        }

        Assert.DoesNotContain("secret-detail", raw);
        Assert.DoesNotContain("secret-model", raw);
        Assert.DoesNotContain("secret-model.example", raw);
        Assert.DoesNotContain("https://secret", raw);
        Assert.DoesNotContain("test-key", raw);
    }

    [Fact]
    public async Task PostAnalyze_ProviderServerError500_ReturnsBadGatewayAiModelProviderError()
    {
        var response = await PostWithThrowingClient(
            new GeminiApiException(HttpStatusCode.InternalServerError, "secret-detail https://secret-model.example"));

        await AssertProblem(response, HttpStatusCode.BadGateway, "AI model provider error",
            "The AI model provider returned an error. Please try again in a moment.");
    }

    [Fact]
    public async Task PostAnalyze_ProviderErrorWithoutStatus_ReturnsBadGatewayAiModelProviderError()
    {
        var response = await PostWithThrowingClient(new GeminiApiException(null, "secret-detail"));

        await AssertProblem(response, HttpStatusCode.BadGateway, "AI model provider error");
    }

    [Fact]
    public async Task PostAnalyze_GitHubNotFound_ReturnsNotFoundRepositoryNotFound()
    {
        var response = await PostWithThrowingClient(new GitHubNotFoundException("secret-detail"));

        await AssertProblem(response, HttpStatusCode.NotFound, "Repository not found",
            "The repository does not exist or is not public.");
    }

    [Fact]
    public async Task PostAnalyze_GitHubRateLimited_ReturnsTooManyRequestsGitHubLimitReached()
    {
        var response = await PostWithThrowingClient(
            new GitHubRateLimitException(HttpStatusCode.Forbidden, "secret-detail", DateTimeOffset.UnixEpoch));

        await AssertProblem(response, HttpStatusCode.TooManyRequests, "GitHub limit reached",
            "The GitHub API limit was reached. Please try again later.");
    }

    [Fact]
    public async Task PostAnalyze_OtherGitHubError_ReturnsBadGatewayGitHubError()
    {
        var response = await PostWithThrowingClient(
            new GitHubApiException(HttpStatusCode.InternalServerError, "secret-detail"));

        await AssertProblem(response, HttpStatusCode.BadGateway, "GitHub error",
            "GitHub API returned an error. Please try again later.");
    }

    [Fact]
    public async Task PostAnalyze_HttpClientTimeoutWhileClientConnected_ReturnsGatewayTimeout()
    {
        var response = await PostWithThrowingClient(
            new TaskCanceledException("secret-detail", new TimeoutException("secret-detail")));

        await AssertProblem(response, HttpStatusCode.GatewayTimeout, "AI model provider timed out",
            "The AI model provider is overloaded, please try again in a few minutes.");
    }

    [Fact]
    public async Task PostAnalyze_ModelNeverAnswersBeforeDeadline_ReturnsGatewayTimeout()
    {
        var original = AnalyzeController.AnalysisDeadline;
        AnalyzeController.AnalysisDeadline = TimeSpan.FromMilliseconds(200);
        try
        {
            var response = await PostWithChatClient(new BlockingChatClient());

            await AssertProblem(response, HttpStatusCode.GatewayTimeout, "AI model provider timed out",
                "The AI model provider is overloaded, please try again in a few minutes.");
        }
        finally
        {
            AnalyzeController.AnalysisDeadline = original;
        }
    }

    private async Task<HttpResponseMessage> PostWithChatClient(IChatClient chat)
    {
        using var client = _factory
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("AiModel:ApiKey", "test-key");
                b.ConfigureTestServices(s =>
                {
                    s.AddSingleton<IGitHubClient>(new FakeGitHubClient());
                    s.AddSingleton(chat);
                });
            })
            .CreateClient();
        return await client.PostAsJsonAsync("/api/analyze", new { repoUrl = ValidUrl, question = "q" });
    }

    private sealed class BlockingChatClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new ChatResponse();
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
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

    [Fact]
    public async Task PostAnalyze_InvalidRepoUrl_ReturnsBadRequestWithRepoUrlError()
    {
        var response = await Post(new { repoUrl = "https://gitlab.com/o/r", question = "q" });

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

    private static async Task AssertValidationError(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _),
            $"Expected errors.{field} in: {body}");
    }
}
