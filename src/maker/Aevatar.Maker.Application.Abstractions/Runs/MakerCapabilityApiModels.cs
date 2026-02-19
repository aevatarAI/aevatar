namespace Aevatar.Maker.Application.Abstractions.Runs;

public sealed record MakerRunInput
{
    public required string WorkflowYaml { get; init; }
    public required string WorkflowName { get; init; }
    public required string Input { get; init; }
    public string? ActorId { get; init; }
    public int? TimeoutSeconds { get; init; }
    public bool DestroyActorAfterRun { get; init; }
}
