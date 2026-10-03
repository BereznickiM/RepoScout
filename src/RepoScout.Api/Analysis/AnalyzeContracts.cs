using System.ComponentModel.DataAnnotations;

namespace RepoScout.Api.Analysis;

public record AnalyzeRequest([Required] string RepoUrl, [Required, MaxLength(500)] string Question);

public record AnalyzeResponse(string Report);
