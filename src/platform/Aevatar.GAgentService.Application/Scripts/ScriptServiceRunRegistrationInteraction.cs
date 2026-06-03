using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeScripts;
using Aevatar.GAgentService.Application.ServiceRuns;
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

    public async Task<CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus>> ExecuteAsync(
        ScriptServiceRunCommand command,
        Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
        Func<ScriptServiceRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(emitAsync);

        ServiceRunRegistrationResult? registeredRun = null;
        var terminalObservation = new ServiceRunTerminalAguiObservation();

        async ValueTask EmitObservedAsync(AGUIEvent aguiEvent, CancellationToken token)
        {
            terminalObservation.Observe(aguiEvent);
            await emitAsync(aguiEvent, token);
        }

        var result = await _inner.ExecuteAsync(
            command,
            EmitObservedAsync,
            async (receipt, token) =>
            {
                registeredRun = await _serviceRunRegistrationPort.RegisterAsync(
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

        await PersistTerminalStatusAsync(registeredRun, result, terminalObservation);
        return result;
    }

    private async Task PersistTerminalStatusAsync(
        ServiceRunRegistrationResult? registeredRun,
        CommandInteractionResult<ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, ScriptServiceRunCompletionStatus> result,
        ServiceRunTerminalAguiObservation terminalObservation)
    {
        if (registeredRun == null ||
            !result.Succeeded ||
            result.FinalizeResult?.Completed != true ||
            !TryMapTerminalStatus(result.FinalizeResult.Completion, terminalObservation, out var status))
        {
            return;
        }

        await _serviceRunRegistrationPort.UpdateStatusAsync(
            registeredRun.RunActorId,
            registeredRun.RunId,
            status,
            terminalObservation.LastOutput,
            terminalObservation.LastError,
            CancellationToken.None);
    }

    private static bool TryMapTerminalStatus(
        ScriptServiceRunCompletionStatus completion,
        ServiceRunTerminalAguiObservation terminalObservation,
        out ServiceRunStatus status)
    {
        if (terminalObservation.HasTerminalObservation)
        {
            status = terminalObservation.Status;
            return status != ServiceRunStatus.Unspecified;
        }

        status = completion switch
        {
            ScriptServiceRunCompletionStatus.RunFinished => ServiceRunStatus.Completed,
            ScriptServiceRunCompletionStatus.RunError => ServiceRunStatus.Failed,
            _ => ServiceRunStatus.Unspecified,
        };
        return status != ServiceRunStatus.Unspecified;
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
