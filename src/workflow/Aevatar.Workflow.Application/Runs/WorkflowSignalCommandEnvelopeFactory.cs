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
        var signal = new SignalReceivedEvent
        {
            RunId = command.RunId,
            StepId = NormalizeOptional(command.StepId),
            SignalName = signalName,
            Payload = command.Payload ?? string.Empty,
        };
        if (command.ExternalApproval != null)
            signal.ExternalApproval = ToExternalApprovalTerminalSignal(command.ExternalApproval);

        return new EventEnvelope
        {
            // Refactor (issue1277/first-slice):
            // Old pattern: signal envelopes could use a factory-local random id.
            // New principle: the accepted command id is the envelope identity.
            // Correlation remains propagation context, not the target envelope id.
            Id = context.CommandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(signal),
            Route = EnvelopeRouteSemantics.CreateDirect("api.workflow.signal", context.TargetId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = context.CorrelationId,
            },
        };
    }

    private static string NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static WorkflowExternalApprovalTerminalSignal ToExternalApprovalTerminalSignal(
        WorkflowExternalApprovalTerminalSignalCommand command)
    {
        var signal = new WorkflowExternalApprovalTerminalSignal
        {
            SourceId = NormalizeOptional(command.SourceId),
            ExternalIdKind = NormalizeOptional(command.ExternalIdKind),
            ExternalId = NormalizeOptional(command.ExternalId),
            InstanceCode = NormalizeOptional(command.InstanceCode),
            RequestId = NormalizeOptional(command.RequestId),
            TerminalStatus = NormalizeOptional(command.TerminalStatus),
            CallbackIdempotencyKey = NormalizeOptional(command.CallbackIdempotencyKey),
        };

        if (command.ProviderDeliveryEvidence != null)
        {
            foreach (var (key, value) in command.ProviderDeliveryEvidence)
            {
                var normalizedKey = NormalizeOptional(key);
                if (normalizedKey.Length == 0)
                    continue;

                signal.ProviderDeliveryEvidence[normalizedKey] = value ?? string.Empty;
            }
        }

        return signal;
    }

    private static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = NormalizeOptional(value);
        return normalized.Length == 0
            ? throw new ArgumentException("Value is required.", paramName)
            : normalized;
    }
}
