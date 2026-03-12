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

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new WorkflowResumedEvent
            {
                RunId = command.RunId,
                StepId = command.StepId,
                Approved = command.Approved,
                UserInput = command.UserInput ?? string.Empty,
            }),
            Route = EnvelopeRouteSemantics.CreateDirect("api.workflow.resume", context.TargetId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = context.CorrelationId,
            },
        };
    }
}
