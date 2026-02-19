using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.CQRS.Core.Commands;

public sealed class DefaultCommandContextPolicy : ICommandContextPolicy
{
    public CommandContext Create(
        string targetId,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? commandId = null,
        string? correlationId = null)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            throw new ArgumentException("Target id is required.", nameof(targetId));

        var resolvedCommandId = string.IsNullOrWhiteSpace(commandId)
            ? Guid.NewGuid().ToString("N")
            : commandId;
        var resolvedCorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? resolvedCommandId
            : correlationId;
        var resolvedMetadata = metadata == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(metadata, StringComparer.Ordinal);

        return new CommandContext(targetId, resolvedCommandId, resolvedCorrelationId, resolvedMetadata);
    }
}
