using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RunScriptRuntimeCommandEnvelopeFactory
    : ICommandEnvelopeFactory<RunScriptRuntimeCommand>
{
    public EventEnvelope CreateEnvelope(
        RunScriptRuntimeCommand command,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        var envelope = ScriptingActorRequestEnvelopeFactory.Create(
            context.TargetId,
            context.CorrelationId,
            new RunScriptRequestedEvent
            {
                RunId = command.RunId ?? string.Empty,
                InputPayload = command.InputPayload?.Clone(),
                ScriptRevision = command.ScriptRevision ?? string.Empty,
                DefinitionActorId = command.DefinitionActorId ?? string.Empty,
                RequestedEventType = command.RequestedEventType ?? string.Empty,
                CommandId = context.CommandId ?? string.Empty,
                CorrelationId = context.CorrelationId ?? string.Empty,
                ScopeId = command.ScopeId ?? string.Empty,
                CompletionNotificationActorId = command.CompletionNotificationActorId ?? string.Empty,
                CompletionNotificationDeliveryId = command.CompletionNotificationDeliveryId ?? string.Empty,
                CompletionNotificationExpiresAtUnixMs = command.CompletionNotificationExpiresAtUnixMs,
            });
        envelope.Id = context.CommandId;
        envelope.EnsureRuntime().EnsureDeliveryIdentity().OperationId = context.CommandId;
        return envelope;
    }
}
