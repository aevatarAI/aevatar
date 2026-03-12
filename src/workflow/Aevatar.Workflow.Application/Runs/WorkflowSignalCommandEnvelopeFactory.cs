using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowSignalCommandEnvelopeFactory : ICommandEnvelopeFactory<WorkflowSignalCommand>
{
    public EventEnvelope CreateEnvelope(WorkflowSignalCommand command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new SignalReceivedEvent
            {
                RunId = command.RunId,
                StepId = command.StepId ?? string.Empty,
                SignalName = command.SignalName,
                Payload = command.Payload ?? string.Empty,
            }),
            Route = new EnvelopeRoute
            {
                PublisherActorId = "api.workflow.signal",
                Direction = EventDirection.Self,
                TargetActorId = context.TargetId,
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = context.CorrelationId,
            },
        };
    }
}
