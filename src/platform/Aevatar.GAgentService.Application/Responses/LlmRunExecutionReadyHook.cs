using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Application.Responses;

public sealed class LlmRunExecutionReadyHook(
    ILlmRunExecutionScheduler scheduler,
    ILogger<LlmRunExecutionReadyHook>? logger = null) : ICommittedStatePublicationHook
{
    private readonly ILogger<LlmRunExecutionReadyHook> _logger = logger ?? NullLogger<LlmRunExecutionReadyHook>.Instance;

    public async Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (context.Published.StateRoot?.Is(LlmSessionState.Descriptor) != true ||
            context.Published.StateEvent?.EventData?.Is(LlmRunExecutionReadyEvent.Descriptor) != true)
        {
            return;
        }

        if (!TryBuildRequest(context, out var request))
        {
            return;
        }

        try
        {
            await scheduler.ScheduleAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "LLM run execution scheduling failed for session actor {SessionActorId} response {ResponseId} run {RunId}.",
                request.SessionActorId,
                request.ResponseId,
                request.RunId);
        }
    }

    private static bool TryBuildRequest(
        CommittedStatePublicationContext context,
        out LlmRunExecutionRequest request)
    {
        request = default!;
        var ready = context.Published.StateEvent!.EventData.Unpack<LlmRunExecutionReadyEvent>();
        if (string.IsNullOrWhiteSpace(ready.ResponseId) || string.IsNullOrWhiteSpace(ready.RunId))
            return false;

        if (ready.ExecutionRequest == null)
            return false;

        var responseId = ready.ResponseId.Trim();
        var runId = ready.RunId.Trim();
        var executionCommand = ready.ExecutionRequest.Clone();
        executionCommand.ResponseId = responseId;
        executionCommand.RunId = runId;

        request = new LlmRunExecutionRequest(
            context.ActorId,
            responseId,
            runId,
            executionCommand,
            ResolveOriginPlatform(context.Published.StateRoot));
        return true;
    }

    private static string? ResolveOriginPlatform(Any? stateRoot)
    {
        if (stateRoot?.Is(LlmSessionState.Descriptor) != true)
            return null;

        var state = stateRoot.Unpack<LlmSessionState>();
        return state.Record == null || state.Record.OriginKind == LlmSessionOriginKind.Unspecified
            ? null
            : state.Record.OriginKind.ToString();
    }
}
