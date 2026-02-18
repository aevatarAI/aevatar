using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.CQRS.Core.Commands;

public sealed class DefaultCommandContextPolicy : ICommandContextPolicy
{
    public CommandContext Create(
        string targetId,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? correlationId = null)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            throw new ArgumentException("Target id is required.", nameof(targetId));

        var resolvedCorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString("N")
            : correlationId;
        var resolvedMetadata = metadata == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(metadata, StringComparer.Ordinal);

        return new CommandContext(targetId, resolvedCorrelationId, resolvedMetadata);
    }

    public bool TryResolve(EventEnvelope envelope, out CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var targetId = string.IsNullOrWhiteSpace(envelope.TargetActorId)
            ? envelope.PublisherId
            : envelope.TargetActorId;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            context = null!;
            return false;
        }

        var correlationId = string.IsNullOrWhiteSpace(envelope.CorrelationId)
            ? Guid.NewGuid().ToString("N")
            : envelope.CorrelationId;
        var metadata = new Dictionary<string, string>(envelope.Metadata, StringComparer.Ordinal);

        context = new CommandContext(targetId, correlationId, metadata);
        return true;
    }
}
