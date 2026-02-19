// ─────────────────────────────────────────────────────────────
// LLMCallModule - LLM call step module.
//
// Receives StepRequestEvent (step_type=llm_call), converts to
// ChatRequestEvent and sends to the specific target RoleGAgent
// via point-to-point SendToAsync (not broadcast Down).
//
// After RoleGAgent completes (TextMessageEndEvent bubbles Up),
// captures it and converts to StepCompletedEvent.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>LLM call module. Sends ChatRequestEvent to a specific RoleGAgent by ID.</summary>
public sealed class LLMCallModule : IEventModule
{
    private readonly Dictionary<string, StepRequestEvent> _pending = [];

    public string Name => "llm_call";
    public int Priority => 10;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor)
                || payload.Is(TextMessageEndEvent.Descriptor)
                || payload.Is(ChatResponseEvent.Descriptor));
    }

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null) return;

        // ─── Handle StepRequestEvent: send ChatRequestEvent to target role ───
        if (payload.Is(StepRequestEvent.Descriptor))
        {
            var request = payload.Unpack<StepRequestEvent>();
            if (request.StepType != "llm_call") return;

            var prompt = request.Input;
            if (request.Parameters.TryGetValue("prompt_prefix", out var prefix) &&
                !string.IsNullOrEmpty(prefix))
            {
                prompt = prefix.TrimEnd() + "\n\n" + prompt;
            }

            // Use per-step message id to avoid collisions across concurrent llm_call steps.
            var chatMessageId = ChatMessageKeys.CreateWorkflowStepMessageId(request.RunId, request.StepId);
            _pending[chatMessageId] = request;

            var targetRole = request.TargetRole;
            var promptPreview = prompt.Length > 200 ? prompt[..200] + "..." : prompt;

            if (!string.IsNullOrEmpty(targetRole) && ctx.Agent is GAgentBase gab)
            {
                // Point-to-point: send ChatRequestEvent directly to the target role actor by ID
                ctx.Logger.LogInformation(
                    "LLMCallModule: step={StepId} → SendTo role={Role} prompt=({Len} chars) {Preview}",
                    request.StepId, targetRole, prompt.Length, promptPreview);

                var chatEvt = new ChatRequestEvent { Prompt = prompt, MessageId = chatMessageId };
                await gab.EventPublisher.SendToAsync(targetRole, chatEvt, ct);
            }
            else
            {
                // No target role: publish Self for WorkflowGAgent's own LLM
                ctx.Logger.LogInformation(
                    "LLMCallModule: step={StepId} → Self (no role) prompt=({Len} chars) {Preview}",
                    request.StepId, prompt.Length, promptPreview);

                await ctx.PublishAsync(new ChatRequestEvent
                {
                    Prompt = prompt, MessageId = chatMessageId,
                }, EventDirection.Self, ct);
            }
            return;
        }

        // ─── Handle TextMessageEndEvent: convert to StepCompletedEvent ───
        if (payload.Is(TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<TextMessageEndEvent>();
            var messageId = evt.MessageId;
            if (string.IsNullOrEmpty(messageId)) return;
            if (!_pending.TryGetValue(messageId, out var pending)) return;
            _pending.Remove(messageId);

            var outputPreview = (evt.Content ?? "").Length > 300 ? evt.Content![..300] + "..." : evt.Content ?? "";
            ctx.Logger.LogInformation(
                "LLMCallModule: step={StepId} completed ({Len} chars): {Preview}",
                pending.StepId, evt.Content?.Length ?? 0, outputPreview);

            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = pending.StepId, RunId = pending.RunId,
                Success = true, Output = evt.Content ?? "",
                WorkerId = envelope.PublisherId,
            }, EventDirection.Self, ct);
            return;
        }

        // ─── Handle ChatResponseEvent (non-streaming fallback) ───
        if (payload.Is(ChatResponseEvent.Descriptor))
        {
            var evt = payload.Unpack<ChatResponseEvent>();
            var messageId = evt.MessageId;
            if (string.IsNullOrEmpty(messageId)) return;
            if (!_pending.TryGetValue(messageId, out var pending)) return;
            _pending.Remove(messageId);

            var nsPreview = (evt.Content ?? "").Length > 300 ? evt.Content![..300] + "..." : evt.Content ?? "";
            ctx.Logger.LogInformation(
                "LLMCallModule: step={StepId} completed non-streaming ({Len} chars): {Preview}",
                pending.StepId, evt.Content?.Length ?? 0, nsPreview);

            await ctx.PublishAsync(new StepCompletedEvent
            {
                StepId = pending.StepId, RunId = pending.RunId,
                Success = true, Output = evt.Content ?? "",
                WorkerId = ctx.AgentId,
            }, EventDirection.Self, ct);
        }
    }
}
