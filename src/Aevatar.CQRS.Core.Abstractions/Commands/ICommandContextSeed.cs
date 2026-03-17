namespace Aevatar.CQRS.Core.Abstractions.Commands;

/// <summary>
/// Optional command-provided context seed for stable command/correlation identity and transport headers.
/// </summary>
public interface ICommandContextSeed
{
    string? CommandId { get; }

    string? CorrelationId { get; }

    IReadOnlyDictionary<string, string>? Headers { get; }
}
