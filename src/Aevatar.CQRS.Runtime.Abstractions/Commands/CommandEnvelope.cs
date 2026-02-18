namespace Aevatar.CQRS.Runtime.Abstractions.Commands;

public sealed record CommandEnvelope(
    string CommandId,
    string CorrelationId,
    string Target,
    DateTimeOffset EnqueuedAt,
    IReadOnlyDictionary<string, string> Metadata)
{
    public static CommandEnvelope Create(
        string commandId,
        string correlationId,
        string target,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(commandId))
            throw new ArgumentException("Command id is required.", nameof(commandId));
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Correlation id is required.", nameof(correlationId));
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("Target is required.", nameof(target));

        return new CommandEnvelope(
            commandId,
            correlationId,
            target,
            DateTimeOffset.UtcNow,
            metadata == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(metadata, StringComparer.Ordinal));
    }
}
