using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RepoScout.Api.IntegrationTests;

public class AnalyzeEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ValidUrl = "https://github.com/o/r";
    private readonly HttpClient _client;

    public AnalyzeEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    private Task<HttpResponseMessage> Post(object body) => _client.PostAsJsonAsync("/api/analyze", body);

    [Fact]
    public async Task PostAnalyze_ValidRequest_ReturnsMockReportWithRepoAndQuestion()
    {
        var response = await Post(new { repoUrl = ValidUrl, question = "What stack is used?" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("report").GetString();
        Assert.Contains("o/r", report);
        Assert.Contains("mocked", report);
        Assert.Contains("> What stack is used?", report);
    }

    [Fact]
    public async Task PostAnalyze_QuestionWithSurroundingWhitespace_IsTrimmedInReport()
    {
        var response = await Post(new { repoUrl = ValidUrl, question = "  hi  " });

        var report = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("report").GetString();
        Assert.Contains("> hi\n", report!.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task PostAnalyze_MultilineQuestion_QuotesEveryLine()
    {
        var response = await Post(new { repoUrl = ValidUrl, question = "line1\nline2" });

        var report = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("report").GetString();
        Assert.Contains("> line1\n> line2", report!.ReplaceLineEndings("\n"));
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
