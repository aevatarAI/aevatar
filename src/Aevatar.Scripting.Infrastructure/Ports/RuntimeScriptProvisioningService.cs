using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions.Context;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptProvisioningService : IScriptRuntimeProvisioningPort
{
    private static readonly TimeSpan BindingQueryTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BindingReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BindingRetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly ICommandDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _dispatchService;
    private readonly RuntimeScriptActorQueryClient? _queryClient;
    private readonly IAgentContextAccessor? _agentContextAccessor;

    public RuntimeScriptProvisioningService(
        ICommandDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> dispatchService)
    {
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
    }

    public RuntimeScriptProvisioningService(
        ICommandDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> dispatchService,
        RuntimeScriptActorQueryClient queryClient,
        IAgentContextAccessor agentContextAccessor)
    {
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
        _agentContextAccessor = agentContextAccessor ?? throw new ArgumentNullException(nameof(agentContextAccessor));
    }

    public async Task<string> EnsureRuntimeAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        CancellationToken ct)
    {
        var result = await _dispatchService.DispatchAsync(
            new ProvisionScriptRuntimeCommand(
                definitionActorId ?? string.Empty,
                scriptRevision ?? string.Empty,
                runtimeActorId),
            ct);
        if (!result.Succeeded)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script runtime provisioning dispatch failed.");

        var receipt = result.Receipt
            ?? throw new InvalidOperationException("Script runtime provisioning did not produce a receipt.");
        var resolvedRuntimeActorId = receipt.ActorId;
        if (_queryClient == null)
            return resolvedRuntimeActorId;

        await WaitUntilBoundAsync(resolvedRuntimeActorId, ct);
        return resolvedRuntimeActorId;
    }

    private async Task WaitUntilBoundAsync(
        string runtimeActorId,
        CancellationToken ct)
    {
        using var readyTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readyTimeoutCts.CancelAfter(BindingReadyTimeout);
        var waitCt = readyTimeoutCts.Token;

        while (true)
        {
            ScriptBehaviorBindingRespondedEvent response;
            try
            {
                response = await _queryClient!.QueryActorAsync<ScriptBehaviorBindingRespondedEvent>(
                    runtimeActorId,
                    ScriptActorQueryRouteConventions.BindingReplyStreamPrefix,
                    BindingQueryTimeout,
                    (requestId, replyStreamId) => ScriptActorQueryEnvelopeFactory.CreateBehaviorBindingQuery(
                        runtimeActorId,
                        requestId,
                        replyStreamId),
                    static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
                    ScriptActorQueryRouteConventions.BuildBindingTimeoutMessage,
                    waitCt);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out waiting for script runtime `{runtimeActorId}` to finish binding.");
            }

            if (response.Found)
                return;

            if (!response.Pending)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(response.FailureReason)
                        ? $"Script runtime `{runtimeActorId}` is not bound."
                        : response.FailureReason);
            }

            try
            {
                await Task.Delay(BindingRetryDelay, waitCt);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out waiting for script runtime `{runtimeActorId}` to finish binding.");
            }
        }
    }
}
