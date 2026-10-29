namespace RepoScout.Api.Agent;

public sealed class AnalysisOptions
{
    public const string SectionName = "Analysis";

    /// <summary>Overall analysis deadline.</summary>
    public TimeSpan Deadline { get; init; }

    public int MaxToolCalls { get; init; }

    public int MaxResultBytes { get; init; }

    public int MaxDepth { get; init; }
}
