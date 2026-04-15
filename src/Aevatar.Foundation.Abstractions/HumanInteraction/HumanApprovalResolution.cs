namespace Aevatar.Foundation.Abstractions.HumanInteraction;

public sealed record HumanApprovalResolution
{
    public required string ActorId { get; init; }

    public required string RunId { get; init; }

    public required string StepId { get; init; }

    public bool Approved { get; init; }

    public string? UserInput { get; init; }

    public string? ResolvedContent { get; init; }
}
