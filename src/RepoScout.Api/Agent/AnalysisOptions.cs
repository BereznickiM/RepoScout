using System.ComponentModel.DataAnnotations;

namespace RepoScout.Api.Agent;

public sealed class AnalysisOptions
{
    public const string SectionName = "Analysis";

    /// <summary>Overall analysis deadline.</summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "01:00:00")]
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(170);

    [Range(1, 1000)]
    public int MaxToolCalls { get; init; } = 20;

    [Range(1, 10_000_000)]
    public int MaxResultBytes { get; init; } = 12_000;

    [Range(1, 100)]
    public int MaxDepth { get; init; } = 2;
}
