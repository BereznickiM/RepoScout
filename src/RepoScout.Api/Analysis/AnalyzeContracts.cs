using System.ComponentModel.DataAnnotations;

namespace RepoScout.Api.Analysis;

public record AnalyzeRequest([Required] string RepoUrl, [Required, MaxLength(500)] string Question);

public sealed record AnalysisUsage(
    int Rounds,
    int ToolCalls,
    IReadOnlyDictionary<string, int> ToolCallsByName,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    long DurationMs,
    bool BudgetExhausted);

public sealed record AnalysisResult(string Report, AnalysisUsage Usage);

public sealed record AnalyzeResponse(string Report, AnalysisUsage Usage);
