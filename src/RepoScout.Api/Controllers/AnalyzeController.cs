using Microsoft.AspNetCore.Mvc;
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

        var report = await analyzer.AnalyzeAsync(repo, request.Question.Trim(), HttpContext.RequestAborted);
        return Ok(new AnalyzeResponse(report));
    }
}
