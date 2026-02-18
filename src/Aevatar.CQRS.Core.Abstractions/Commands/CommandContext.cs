namespace Aevatar.CQRS.Core.Abstractions.Commands;

public sealed record CommandContext(
    string TargetId,
    string CommandId,
    string CorrelationId,
    IReadOnlyDictionary<string, string> Metadata);
