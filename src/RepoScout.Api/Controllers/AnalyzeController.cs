using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RepoScout.Api.Agent;
using RepoScout.Api.Analysis;
using RepoScout.Api.GitHub;

namespace RepoScout.Api.Controllers;

[ApiController]
[Route("api/analyze")]
public class AnalyzeController(IRepoAnalyzer analyzer, IOptions<AnalysisOptions> options) : ControllerBase
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

        var result = await analyzer.AnalyzeAsync(repo, request.Question.Trim(), deadline.Token);
        return Ok(new AnalyzeResponse(result.Report, result.Usage));
    }
}
