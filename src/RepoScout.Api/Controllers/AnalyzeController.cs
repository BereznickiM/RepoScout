using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RepoScout.Api.Agent;
using RepoScout.Api.Ai;
using RepoScout.Api.Analysis;
using RepoScout.Api.GitHub;

namespace RepoScout.Api.Controllers;

[ApiController]
[Route("api/analyze")]
public class AnalyzeController(
    IRepoAnalyzer analyzer, IOptions<AnalysisOptions> options, ILogger<AnalyzeController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post(AnalyzeRequest request)
    {
        if (!GitHubRepoUrl.TryParse(request.RepoUrl, out var repo))
        {
            ModelState.AddModelError(nameof(AnalyzeRequest.RepoUrl), "Enter a valid GitHub repository URL, e.g. https://github.com/owner/repo.");
            return ValidationProblem();
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        deadline.CancelAfter(options.Value.Deadline);

        try
        {
            var result = await analyzer.AnalyzeAsync(repo, request.Question.Trim(), deadline.Token);
            return Ok(new AnalyzeResponse(result.Report, result.Usage));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException
                                   && !HttpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Analysis timed out");
            return Problem(
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "AI model provider timed out",
                detail: "The AI model provider is overloaded, please try again in a few minutes.");
        }
        catch (AiRateLimitException ex)
        {
            logger.LogWarning(ex, "AI model rate limit reached");
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Limit reached",
                detail: "The AI model's usage limit was reached. Please try again later.");
        }
        catch (GeminiApiException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            logger.LogWarning(ex, "AI model overloaded");
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "AI model provider unavailable",
                detail: "The AI model provider is unavailable or overloaded, please try again in a few minutes.");
        }
        catch (GeminiApiException ex)
        {
            logger.LogWarning(ex, "AI model returned an error");
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "AI model provider error",
                detail: "The AI model provider returned an error. Please try again in a moment.");
        }
        catch (GitHubNotFoundException ex)
        {
            logger.LogWarning(ex, "Repository not found");
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Repository not found",
                detail: "The repository does not exist or is not public.");
        }
        catch (GitHubRateLimitException ex)
        {
            logger.LogWarning(ex, "GitHub rate limit reached");
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "GitHub limit reached",
                detail: "The GitHub API limit was reached. Please try again later.");
        }
        catch (GitHubApiException ex)
        {
            logger.LogWarning(ex, "GitHub API error");
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "GitHub error",
                detail: "GitHub API returned an error. Please try again later.");
        }
    }
}
