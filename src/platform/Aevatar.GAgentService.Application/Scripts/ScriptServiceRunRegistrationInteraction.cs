using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeScripts;
using Aevatar.AGUI.Contracts;

namespace Aevatar.GAgentService.Application.Scripts;

// Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
//   Old pattern: Scope service script stream inline orchestration in endpoints
//   New principle: use existing ICommandInteractionService skeleton with ScriptServiceRunCommand and Application-owned service-run registration decorator
public sealed class ScriptServiceRunRegistrationInteraction
    : ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>
{
    private readonly ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus> _inner;
    private readonly IServiceRunRegistrationPort _serviceRunRegistrationPort;

    public ScriptServiceRunRegistrationInteraction(
        ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus> inner,
        IServiceRunRegistrationPort serviceRunRegistrationPort)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _serviceRunRegistrationPort = serviceRunRegistrationPort ?? throw new ArgumentNullException(nameof(serviceRunRegistrationPort));
    }

    public Task<CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>> ExecuteAsync(
        ScriptServiceRunCommand command,
        Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
        Func<ScriptServiceRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(emitAsync);

        return _inner.ExecuteAsync(
            command,
            emitAsync,
            async (receipt, token) =>
            {
                await _serviceRunRegistrationPort.RegisterAsync(
                    new ServiceRunRecord
                    {
                        ScopeId = command.ScopeId ?? string.Empty,
                        ServiceId = command.ServiceId ?? string.Empty,
                        ServiceKey = command.ServiceKey ?? string.Empty,
                        RunId = receipt.RunId,
                        CommandId = receipt.CommandId,
                        CorrelationId = receipt.CorrelationId,
                        EndpointId = command.EndpointId ?? string.Empty,
                        ImplementationKind = ServiceImplementationKind.Scripting,
                        TargetActorId = receipt.ActorId,
                        RevisionId = command.RevisionId ?? string.Empty,
                        DeploymentId = command.DeploymentId ?? string.Empty,
                        Status = ServiceRunStatus.Accepted,
                        Identity = command.Identity?.Clone(),
                    },
                    token);

                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, token);
            },
            ct);
    }

    async Task<RealtimeSessionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>> IRealtimeSession<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>.ExecuteAsync(
        ScriptServiceRunCommand inbound,
        Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
        Func<ScriptServiceRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
        CancellationToken ct)
    {
        return await ExecuteAsync(inbound, emitAsync, onAcceptedAsync, ct);
    }
}
