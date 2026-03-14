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

        var signalName = NormalizeRequired(command.SignalName, nameof(command.SignalName));
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new SignalReceivedEvent
            {
                RunId = command.RunId,
                StepId = NormalizeOptional(command.StepId),
                SignalName = signalName,
                Payload = command.Payload ?? string.Empty,
            }),
            Route = EnvelopeRouteSemantics.CreateDirect("api.workflow.signal", context.TargetId),
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
