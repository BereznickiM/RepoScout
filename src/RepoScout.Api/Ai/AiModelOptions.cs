using System.ComponentModel.DataAnnotations;

namespace RepoScout.Api.Ai;

public sealed class AiModelOptions
{
    public const string SectionName = "AiModel";

    [Required]
    public string Model { get; init; } = "";

    [Required]
    public string ApiKey { get; init; } = "";
}
