using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using RepoScout.Api.Ai;
using RepoScout.Api.GitHub;

namespace RepoScout.Api.Controllers;

public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (Map(exception, httpContext) is not var (status, title, detail))
        {
            return false;
        }

        logger.LogWarning(exception, "Request failed: {Title}", title);
        httpContext.Response.StatusCode = status;

        return await problemDetails.TryWriteAsync(new()
        {
            HttpContext = httpContext,
            ProblemDetails = { Status = status, Title = title, Detail = detail }
        });
    }

    private static (int Status, string Title, string Detail)? Map(Exception ex, HttpContext ctx) => ex switch
    {
        OperationCanceledException or TimeoutException when !ctx.RequestAborted.IsCancellationRequested => (
            StatusCodes.Status504GatewayTimeout,
            "AI model provider timed out",
            "The AI model provider is overloaded, please try again in a few minutes."),
        AiRateLimitException => (
            StatusCodes.Status429TooManyRequests,
            "Limit reached",
            "The AI model's usage limit was reached. Please try again later."),
        GeminiApiException { StatusCode: HttpStatusCode.ServiceUnavailable } => (
            StatusCodes.Status503ServiceUnavailable,
            "AI model provider unavailable",
            "The AI model provider is unavailable or overloaded, please try again in a few minutes."),
        GeminiApiException => (
            StatusCodes.Status502BadGateway,
            "AI model provider error",
            "The AI model provider returned an error. Please try again in a moment."),
        GitHubNotFoundException => (
            StatusCodes.Status404NotFound,
            "Repository not found",
            "The repository does not exist or is not public."),
        GitHubRateLimitException => (
            StatusCodes.Status429TooManyRequests,
            "GitHub limit reached",
            "The GitHub API limit was reached. Please try again later."),
        GitHubApiException => (
            StatusCodes.Status502BadGateway,
            "GitHub error",
            "GitHub API returned an error. Please try again later."),
        _ => null
    };
}
