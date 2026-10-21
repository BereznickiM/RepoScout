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
                detail: "The AI service is currently rate-limited. Please try again later.");
        }
    }
}
