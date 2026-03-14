using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptProvisioningService : IScriptRuntimeProvisioningPort
{
    private static readonly TimeSpan ProjectionObservationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProjectionObservationPollInterval = TimeSpan.FromMilliseconds(20);

    private readonly ICommandDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _dispatchService;
    private readonly IScriptDefinitionSnapshotPort _definitionSnapshotPort;

    public RuntimeScriptProvisioningService(
        ICommandDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> dispatchService,
        IScriptDefinitionSnapshotPort definitionSnapshotPort)
    {
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
        _definitionSnapshotPort = definitionSnapshotPort ?? throw new ArgumentNullException(nameof(definitionSnapshotPort));
    }

    public async Task<string> EnsureRuntimeAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        CancellationToken ct,
        ScriptDefinitionSnapshot? definitionSnapshot = null)
    {
        var resolvedDefinitionSnapshot = definitionSnapshot
            ?? await WaitForSnapshotAsync(
                definitionActorId,
                scriptRevision,
                ct);
        var result = await _dispatchService.DispatchAsync(
            new ProvisionScriptRuntimeCommand(
                definitionActorId ?? string.Empty,
                scriptRevision ?? string.Empty,
                runtimeActorId,
                resolvedDefinitionSnapshot),
            ct);
        if (!result.Succeeded)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script runtime provisioning dispatch failed.");

        var receipt = result.Receipt
            ?? throw new InvalidOperationException("Script runtime provisioning did not produce a receipt.");
        return receipt.ActorId;
    }

    private async Task<ScriptDefinitionSnapshot> WaitForSnapshotAsync(
        string definitionActorId,
        string scriptRevision,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProjectionObservationTimeout);

        try
        {
            while (true)
            {
                var snapshot = await _definitionSnapshotPort.TryGetAsync(
                    definitionActorId,
                    scriptRevision,
                    timeoutCts.Token);
                if (snapshot != null)
                    return snapshot;

                await Task.Delay(ProjectionObservationPollInterval, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for script definition snapshot observation. actor_id={definitionActorId}, revision={scriptRevision}");
        }
    }
}
