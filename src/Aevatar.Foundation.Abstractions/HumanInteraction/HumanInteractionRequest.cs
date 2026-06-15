namespace Aevatar.Foundation.Abstractions.HumanInteraction;

public sealed record HumanInteractionRequest
{
    public required string ActorId { get; init; }

    public required string RunId { get; init; }

    public required string StepId { get; init; }

    public required string SuspensionType { get; init; }

    public required string Prompt { get; init; }

    public string? Content { get; init; }

    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();

    public int TimeoutSeconds { get; init; }

    public IReadOnlyDictionary<string, string> Annotations { get; init; } = new Dictionary<string, string>();
}
