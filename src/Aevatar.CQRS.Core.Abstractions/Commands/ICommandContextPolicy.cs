namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface ICommandContextPolicy
{
    CommandContext Create(
        string targetId,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? commandId = null,
        string? correlationId = null);

    bool TryResolve(EventEnvelope envelope, out CommandContext context);
}
