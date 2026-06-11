using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowResumeCommandEnvelopeFactory : ICommandEnvelopeFactory<WorkflowResumeCommand>
{
    public EventEnvelope CreateEnvelope(WorkflowResumeCommand command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        var stepId = NormalizeRequired(command.StepId, nameof(command.StepId));
        var resumed = new WorkflowResumedEvent
        {
            RunId = command.RunId,
            StepId = stepId,
            Approved = command.Approved,
            UserInput = command.UserInput ?? string.Empty,
            EditedContent = command.EditedContent ?? string.Empty,
            Feedback = command.Feedback ?? string.Empty,
            ExecutionId = NormalizeOptional(command.ExecutionId),
            ApprovalRequestId = NormalizeOptional(command.ApprovalRequestId),
        };
        AppendMetadata(resumed.Metadata, command.Metadata);

        return new EventEnvelope
        {
            // Refactor (issue1277/first-slice):
            // Old pattern: resume envelopes could use a factory-local random id.
            // New principle: the accepted command id is the envelope identity.
            // Correlation remains propagation context, not the target envelope id.
            Id = context.CommandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(resumed),
            Route = EnvelopeRouteSemantics.CreateDirect("api.workflow.resume", context.TargetId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = context.CorrelationId,
            },
        };
    }

    private static void AppendMetadata(
        Google.Protobuf.Collections.MapField<string, string> destination,
        IReadOnlyDictionary<string, string>? source)
    {
        if (source == null || source.Count == 0)
            return;

        foreach (var (key, value) in source)
        {
            var normalizedKey = string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim();
            var normalizedValue = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
                continue;

            destination[normalizedKey] = normalizedValue;
        }
    }

    private static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        return normalized.Length == 0
            ? throw new ArgumentException("Value is required.", paramName)
            : normalized;
    }

    private static string NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
