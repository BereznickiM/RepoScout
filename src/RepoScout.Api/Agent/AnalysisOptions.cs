using System.ComponentModel.DataAnnotations;

namespace RepoScout.Api.Agent;

public sealed class AnalysisOptions
{
    public const string SectionName = "Analysis";

    /// <summary>Overall analysis deadline.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "1.00:00:00")]
    public TimeSpan Deadline { get; init; }

    [Range(1, int.MaxValue)]
    public int MaxToolCalls { get; init; }

    [Range(1, int.MaxValue)]
    public int MaxResultBytes { get; init; }

    [Range(1, int.MaxValue)]
    public int MaxDepth { get; init; }
}
