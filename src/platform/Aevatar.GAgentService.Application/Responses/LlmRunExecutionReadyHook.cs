using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.Responses;

public sealed class LlmRunExecutionReadyHook(
    ILlmRunExecutor executor,
    ILogger<LlmRunExecutionReadyHook> logger) : ICommittedStatePublicationHook
{
    public Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (context.Published.StateRoot?.Is(LlmSessionState.Descriptor) != true ||
            context.Published.StateEvent?.EventData?.Is(LlmRunExecutionReadyEvent.Descriptor) != true)
        {
            return Task.CompletedTask;
        }

        if (!TryBuildRequest(context, out var request))
            return Task.CompletedTask;

        _ = Task.Run(() => ExecuteAndLogAsync(request), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task ExecuteAndLogAsync(LlmRunExecutorRequest request)
    {
        try
        {
            await executor.ExecuteAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to start off-turn LLM execution for session actor {SessionActorId} run {RunId}.",
                request.SessionActorId,
                request.RunId);
        }
    }

    private static bool TryBuildRequest(
        CommittedStatePublicationContext context,
        out LlmRunExecutorRequest request)
    {
        request = default!;
        var ready = context.Published.StateEvent!.EventData.Unpack<LlmRunExecutionReadyEvent>();
        if (string.IsNullOrWhiteSpace(ready.ResponseId) || string.IsNullOrWhiteSpace(ready.RunId))
            return false;

        if (!TryUnpackCommand(context.SourceEnvelope?.Payload, out var command))
            return false;

        var responseId = ready.ResponseId.Trim();
        var runId = ready.RunId.Trim();
        var executionCommand = command.Clone();
        executionCommand.ResponseId = responseId;
        executionCommand.RunId = runId;

        request = new LlmRunExecutorRequest(
            context.ActorId,
            responseId,
            runId,
            executionCommand,
            ResolveOriginPlatform(context.Published.StateRoot));
        return true;
    }

    private static bool TryUnpackCommand(Any? sourcePayload, out LlmRunRequested command)
    {
        command = default!;
        if (sourcePayload == null)
            return false;

        if (sourcePayload.Is(RecordLlmRunStarted.Descriptor))
        {
            var startedCommand = sourcePayload.Unpack<RecordLlmRunStarted>();
            if (startedCommand.Command == null)
                return false;

            command = startedCommand.Command;
            return true;
        }

        if (sourcePayload.Is(LlmRunRequested.Descriptor))
        {
            command = sourcePayload.Unpack<LlmRunRequested>();
            return true;
        }

        return false;
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
