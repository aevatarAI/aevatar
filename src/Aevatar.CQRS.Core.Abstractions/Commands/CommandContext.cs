namespace Aevatar.CQRS.Core.Abstractions.Commands;

public sealed record CommandContext(
    string TargetId,
    string CorrelationId,
    IReadOnlyDictionary<string, string> Metadata);
