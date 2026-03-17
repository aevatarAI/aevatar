namespace Aevatar.CQRS.Core.Abstractions.Commands;

public sealed record CommandDispatchExecution<TTarget, TReceipt>
    where TTarget : class, ICommandDispatchTarget
{
    public required TTarget Target { get; init; }
    public required CommandContext Context { get; init; }
    public required EventEnvelope Envelope { get; init; }
    public required TReceipt Receipt { get; init; }
}
