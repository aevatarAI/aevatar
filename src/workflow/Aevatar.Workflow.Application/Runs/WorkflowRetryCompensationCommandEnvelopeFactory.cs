using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRetryCompensationCommandEnvelopeFactory
    : ICommandEnvelopeFactory<WorkflowRetryCompensationCommand>
{
    public EventEnvelope CreateEnvelope(WorkflowRetryCompensationCommand command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return new EventEnvelope
        {
            Id = context.CommandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new WorkflowCompensationRetryRequestedEvent
            {
                RunId = command.RunId,
                FailedCompensationStepId = NormalizeRequired(
                    command.FailedCompensationStepId,
                    nameof(command.FailedCompensationStepId)),
                Reason = NormalizeOptional(command.Reason),
                CommandId = context.CommandId,
                CorrelationId = context.CorrelationId,
            }),
            Route = EnvelopeRouteSemantics.CreateDirect("api.workflow.retry-compensation", context.TargetId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = context.CorrelationId,
            },
        };
    }

    private static string NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = NormalizeOptional(value);
        return normalized.Length == 0
            ? throw new ArgumentException("Value is required.", paramName)
            : normalized;
    }
}
