using System.Net;
using Microsoft.AspNetCore.Mvc;
using RepoScout.Api.Ai;
using RepoScout.Api.Analysis;

namespace RepoScout.Api.Controllers;

[ApiController]
[Route("api/analyze")]
public class AnalyzeController(IRepoAnalyzer analyzer) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post(AnalyzeRequest request)
    {
        if (!GitHubRepoUrl.TryParse(request.RepoUrl, out var repo))
        {
            ModelState.AddModelError(nameof(AnalyzeRequest.RepoUrl), "Enter a valid GitHub repository URL, e.g. https://github.com/owner/repo.");
            return ValidationProblem();
        }

        try
        {
            var result = await analyzer.AnalyzeAsync(repo, request.Question.Trim(), HttpContext.RequestAborted);
            return Ok(new AnalyzeResponse(result.Report, result.Usage));
        }
        catch (AiRateLimitException)
        {
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Limit reached",
                detail: "Usage limits reached, please try again later.");
        }
        catch (GeminiApiException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "LLM provider overloaded",
                detail: "LLM provider is overloaded, please try again in few minutes.");
        }
    }
}
