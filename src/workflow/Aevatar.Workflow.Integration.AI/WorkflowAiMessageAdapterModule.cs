using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;
using Google.Protobuf.Collections;

namespace Aevatar.Workflow.Integration.AI;

// Refactor (iter129/cluster-triage-workflow-llm-nyx-coupling): Old pattern: Workflow.Core constructed AI provider DTOs and NyxID fields directly. New principle: this boundary adapter maps workflow-owned LLM intent to AI provider contracts.
public sealed class WorkflowAiMessageAdapterModule(
    IWorkflowLlmInvocationPort invocationPort) : IEventModule<IWorkflowExecutionContext>
{
    public string Name => "workflow_ai_message_adapter";

    public int Priority => 4;

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(WorkflowLlmExecutionIntent.Descriptor) ||
                payload.Is(WorkflowChatRequestEvent.Descriptor) ||
                payload.Is(ChatResponseEvent.Descriptor) ||
                payload.Is(TextMessageEndEvent.Descriptor));
    }

    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return;

        if (payload.Is(WorkflowLlmExecutionIntent.Descriptor))
        {
            await InvokeAndPublishAsync(payload.Unpack<WorkflowLlmExecutionIntent>(), ctx, ct);
            return;
        }

        if (payload.Is(WorkflowChatRequestEvent.Descriptor))
        {
            await HandleWorkflowChatRequestAsync(payload.Unpack<WorkflowChatRequestEvent>(), ctx, ct);
            return;
        }

        if (payload.Is(ChatResponseEvent.Descriptor))
        {
            var evt = payload.Unpack<ChatResponseEvent>();
            await ctx.PublishAsync(new WorkflowChatResponseEvent
            {
                Content = evt.Content ?? string.Empty,
                SessionId = evt.SessionId ?? string.Empty,
            }, TopologyAudience.Self, ct);
            return;
        }

        if (payload.Is(TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<TextMessageEndEvent>();
            await ctx.PublishAsync(new WorkflowTextMessageEndEvent
            {
                Content = evt.Content ?? string.Empty,
                SessionId = evt.SessionId ?? string.Empty,
            }, TopologyAudience.Self, ct);
        }
    }

    private async Task HandleWorkflowChatRequestAsync(
        WorkflowChatRequestEvent request,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (request.LlmIntent != null)
        {
            await InvokeAndPublishAsync(request.LlmIntent, ctx, ct);
            return;
        }

        await ctx.PublishAsync(ToAiChatRequest(request), TopologyAudience.Self, ct);
    }

    private async Task InvokeAndPublishAsync(
        WorkflowLlmExecutionIntent intent,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        await ctx.PublishAsync(new WorkflowLlmInvocationStartedEvent
        {
            RunId = intent.RunId ?? string.Empty,
            StepId = intent.StepId ?? string.Empty,
            SessionId = intent.SessionId ?? string.Empty,
            TargetRole = intent.TargetRole ?? string.Empty,
        }, TopologyAudience.Self, ct);

        await foreach (var streamEvent in invocationPort.InvokeAsync(intent, ct).WithCancellation(ct))
        {
            await PublishWorkflowLlmEventAsync(ctx, streamEvent.Payload, ct);
        }
    }

    private static Task PublishWorkflowLlmEventAsync(
        IWorkflowExecutionContext ctx,
        IMessage payload,
        CancellationToken ct) =>
        payload switch
        {
            WorkflowLlmTextDeltaEvent evt => ctx.PublishAsync(evt, TopologyAudience.Self, ct),
            WorkflowLlmReasoningDeltaEvent evt => ctx.PublishAsync(evt, TopologyAudience.Self, ct),
            WorkflowLlmToolCallDeltaEvent evt => ctx.PublishAsync(evt, TopologyAudience.Self, ct),
            WorkflowLlmToolResultEvent evt => ctx.PublishAsync(evt, TopologyAudience.Self, ct),
            WorkflowLlmInvocationCompletedEvent evt => ctx.PublishAsync(evt, TopologyAudience.Self, ct),
            _ => throw new InvalidOperationException($"Unsupported workflow LLM stream event type '{payload.Descriptor.FullName}'."),
        };

    private static ChatRequestEvent ToAiChatRequest(WorkflowChatRequestEvent request)
    {
        var chat = new ChatRequestEvent
        {
            Prompt = request.Prompt ?? string.Empty,
            SessionId = request.SessionId ?? string.Empty,
            ScopeId = request.ScopeId ?? string.Empty,
        };
        Copy(request.Headers, chat.Headers);
        Copy(request.Metadata, chat.Metadata);
        chat.InputParts.Add(request.InputParts.Select(ToAiPart));
        return chat;
    }

    private static ChatContentPart ToAiPart(WorkflowChatContentPart part) =>
        new()
        {
            Kind = part.Kind switch
            {
                WorkflowChatContentPartKind.Text => ChatContentPartKind.Text,
                WorkflowChatContentPartKind.Image => ChatContentPartKind.Image,
                WorkflowChatContentPartKind.Audio => ChatContentPartKind.Audio,
                WorkflowChatContentPartKind.Video => ChatContentPartKind.Video,
                _ => ChatContentPartKind.Unspecified,
            },
            Text = part.Text ?? string.Empty,
            DataBase64 = part.DataBase64 ?? string.Empty,
            MediaType = part.MediaType ?? string.Empty,
            Uri = part.Uri ?? string.Empty,
            Name = part.Name ?? string.Empty,
        };

    private static void Copy(MapField<string, string> source, MapField<string, string> destination)
    {
        foreach (var (key, value) in source)
            destination[key] = value;
    }
}
