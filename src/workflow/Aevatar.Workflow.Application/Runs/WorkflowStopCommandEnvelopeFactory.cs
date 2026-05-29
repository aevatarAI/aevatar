using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowStopCommandEnvelopeFactory : ICommandEnvelopeFactory<WorkflowStopCommand>
{
    public EventEnvelope CreateEnvelope(WorkflowStopCommand command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return new EventEnvelope
        {
            // Refactor (issue1277/first-slice):
            // Old pattern: stop envelopes could use a factory-local random id.
            // New principle: the accepted command id is the envelope identity.
            // Correlation remains propagation context, not the target envelope id.
            Id = context.CommandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new WorkflowStoppedEvent
            {
                RunId = command.RunId,
                Reason = NormalizeOptional(command.Reason),
            }),
            Route = EnvelopeRouteSemantics.CreateDirect("api.workflow.stop", context.TargetId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = context.CorrelationId,
            },
        };
    }

    private static string NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
