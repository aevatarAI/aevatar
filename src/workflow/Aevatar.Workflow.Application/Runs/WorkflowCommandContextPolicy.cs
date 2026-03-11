using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.Workflow.Application.Runs;

/// <summary>
/// Workflow-local default command context policy.
/// Keeps Application layer independent from CQRS.Core implementation assembly.
/// </summary>
public sealed class WorkflowCommandContextPolicy : ICommandContextPolicy
{
    public CommandContext Create(
        string targetId,
        IReadOnlyDictionary<string, string>? headers = null,
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
        var resolvedHeaders = headers == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(headers, StringComparer.Ordinal);

        return new CommandContext(targetId, resolvedCommandId, resolvedCorrelationId, resolvedHeaders);
    }
}
