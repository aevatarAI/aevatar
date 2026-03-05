namespace Sisyphus.Application.Models.Research;

public sealed class ResearchOptions
{
    public const string SectionName = "Sisyphus:Research";

    /// <summary>Max LLM retries per round before skipping.</summary>
    public int LlmMaxRetries { get; set; } = 3;

    /// <summary>Number of new nodes to generate per round.</summary>
    public int NodesPerRound { get; set; } = 5;
}
