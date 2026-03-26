using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AppPlatform.Hosting.Endpoints;

internal static class AppFunctionAguiEventMapper
{
    public static readonly TypeRegistry TypeRegistry = WorkflowJsonTypeRegistry.Create(AGUIEvent.Descriptor.File);

    public static AGUIEvent BuildRunContextEvent(WorkflowChatRunAcceptedReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        return new AGUIEvent
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Custom = new CustomEvent
            {
                Name = "aevatar.run.context",
                Payload = Any.Pack(new WorkflowRunContextPayload
                {
                    ActorId = receipt.ActorId,
                    WorkflowName = receipt.WorkflowName,
                    CommandId = receipt.CommandId,
                }),
            },
        };
    }

    public static AGUIEvent BuildRunErrorEvent(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new AGUIEvent
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RunError = new RunErrorEvent
            {
                Message = exception.Message,
                Code = "EXECUTION_FAILED",
            },
        };
    }

    public static bool TryMap(WorkflowRunEventEnvelope frame, out AGUIEvent? aguiEvent)
    {
        ArgumentNullException.ThrowIfNull(frame);

        aguiEvent = new AGUIEvent();
        if (frame.Timestamp != null)
            aguiEvent.Timestamp = frame.Timestamp.Value;

        switch (frame.EventCase)
        {
            case WorkflowRunEventEnvelope.EventOneofCase.RunStarted:
                aguiEvent.RunStarted = new RunStartedEvent
                {
                    ThreadId = frame.RunStarted.ThreadId,
                    RunId = ResolveRunId(frame.RunStarted.RunId, frame.RunStarted.ThreadId),
                };
                return true;
            case WorkflowRunEventEnvelope.EventOneofCase.RunFinished:
                aguiEvent.RunFinished = new RunFinishedEvent
                {
                    ThreadId = frame.RunFinished.ThreadId,
                    RunId = ResolveRunId(null, frame.RunFinished.ThreadId),
                    Result = frame.RunFinished.Result,
                };
                return true;
            case WorkflowRunEventEnvelope.EventOneofCase.RunError:
                aguiEvent.RunError = new RunErrorEvent
                {
                    Message = frame.RunError.Message,
                    Code = frame.RunError.Code,
                };
                return true;
            case WorkflowRunEventEnvelope.EventOneofCase.RunStopped:
                aguiEvent.Custom = new CustomEvent
                {
                    Name = "aevatar.run.stopped",
                    Payload = Any.Pack(frame.RunStopped),
                };
                return true;
            case WorkflowRunEventEnvelope.EventOneofCase.StepStarted:
                aguiEvent.StepStarted = new StepStartedEvent
                {
                    StepName = frame.StepStarted.StepName,
                };
                return true;
            case WorkflowRunEventEnvelope.EventOneofCase.StepFinished:
                aguiEvent.StepFinished = new StepFinishedEvent
                {
                    StepName = frame.StepFinished.StepName,
                };
                return true;
            case WorkflowRunEventEnvelope.EventOneofCase.TextMessageStart:
                aguiEvent.TextMessageStart = new TextMessageStartEvent
                {
                    MessageId = frame.TextMessageStart.MessageId,
                    Role = frame.TextMessageStart.Role,
                };
                return true;
            case WorkflowRunEventEnvelope.EventOneofCase.TextMessageContent:
                aguiEvent.TextMessageContent = new TextMessageContentEvent
                {
                    MessageId = frame.TextMessageContent.MessageId,
                    Delta = frame.TextMessageContent.Delta,
                };
                return true;
            case WorkflowRunEventEnvelope.EventOneofCase.TextMessageEnd:
                aguiEvent.TextMessageEnd = new TextMessageEndEvent
                {
                    MessageId = frame.TextMessageEnd.MessageId,
                };
                return true;
            case WorkflowRunEventEnvelope.EventOneofCase.StateSnapshot:
                aguiEvent.StateSnapshot = new StateSnapshotEvent
                {
                    Snapshot = frame.StateSnapshot.Snapshot,
                };
                return true;
            case WorkflowRunEventEnvelope.EventOneofCase.ToolCallStart:
                aguiEvent.ToolCallStart = new ToolCallStartEvent
                {
                    ToolCallId = frame.ToolCallStart.ToolCallId,
                    ToolName = frame.ToolCallStart.ToolName,
                };
                return true;
            case WorkflowRunEventEnvelope.EventOneofCase.ToolCallEnd:
                aguiEvent.ToolCallEnd = new ToolCallEndEvent
                {
                    ToolCallId = frame.ToolCallEnd.ToolCallId,
                    Result = frame.ToolCallEnd.Result,
                };
                return true;
            case WorkflowRunEventEnvelope.EventOneofCase.Custom:
                return TryMapCustom(frame.Custom, aguiEvent);
            default:
                aguiEvent = null;
                return false;
        }
    }

    private static bool TryMapCustom(WorkflowCustomEventPayload custom, AGUIEvent aguiEvent)
    {
        ArgumentNullException.ThrowIfNull(custom);
        ArgumentNullException.ThrowIfNull(aguiEvent);

        if (custom.Payload?.Is(WorkflowHumanInputRequestCustomPayload.Descriptor) == true)
        {
            var payload = custom.Payload.Unpack<WorkflowHumanInputRequestCustomPayload>();
            aguiEvent.HumanInputRequest = new HumanInputRequestEvent
            {
                StepId = payload.StepId,
                RunId = payload.RunId,
                SuspensionType = payload.SuspensionType,
                Prompt = payload.Prompt,
                TimeoutSeconds = payload.TimeoutSeconds,
                VariableName = payload.VariableName,
            };
            return true;
        }

        if (custom.Payload?.Is(WorkflowHumanInputResponseCustomPayload.Descriptor) == true)
        {
            var payload = custom.Payload.Unpack<WorkflowHumanInputResponseCustomPayload>();
            aguiEvent.HumanInputResponse = new HumanInputResponseEvent
            {
                StepId = payload.StepId,
                RunId = payload.RunId,
                Approved = payload.Approved,
                UserInput = payload.UserInput,
            };
            return true;
        }

        aguiEvent.Custom = new CustomEvent
        {
            Name = custom.Name,
            Payload = custom.Payload,
        };
        return true;
    }

    private static string ResolveRunId(string? runId, string threadId) =>
        !string.IsNullOrWhiteSpace(runId)
            ? runId
            : string.IsNullOrWhiteSpace(threadId)
                ? string.Empty
                : threadId;
}
