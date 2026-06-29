using Aevatar.Foundation.Abstractions.Interactions;

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

    public InteractionSpec? InteractionSpec { get; init; }

    public int TimeoutSeconds { get; init; }

    public HumanInteractionTimeoutDefaultDecision TimeoutDefaultDecision { get; init; } =
        HumanInteractionTimeoutDefaultDecision.Unspecified;

    public HumanInteractionCallback? Callback { get; init; }

    public IReadOnlyDictionary<string, string> Annotations { get; init; } = new Dictionary<string, string>();
}

public enum HumanInteractionTimeoutDefaultDecision
{
    Unspecified = 0,
    Reject = 1,
    Approve = 2,
}

public sealed record HumanInteractionCallback
{
    public required string Kind { get; init; }

    public required string ActorId { get; init; }

    public required string RunId { get; init; }

    public required string StepId { get; init; }
}
