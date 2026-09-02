using System.Globalization;
using Aevatar.AI.Abstractions;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Audit;

/// <summary>
/// Committed-fact audit translator for role-chat session completion. Records only
/// safe session-lifecycle facts — session id, model, role id, token COUNTS and
/// tool COUNTS. It never records prompt, response, reasoning, tool-call arguments
/// or results (docs/canon/audit-trail.md structural content exclusion).
/// </summary>
public sealed class RoleChatSessionCompletedAuditTranslator
    : RoleSessionAuditTranslatorBase<RoleChatSessionCompletedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(RoleChatSessionCompletedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        RoleChatSessionCompletedEvent evt)
    {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["model"] = evt.Model ?? string.Empty,
            ["role_id"] = evt.RoleId ?? string.Empty,
            ["tool_call_count"] = evt.ToolCalls.Count.ToString(CultureInfo.InvariantCulture),
            ["tool_receipt_count"] = evt.ToolReceipts.Count.ToString(CultureInfo.InvariantCulture),
            ["content_emitted"] = evt.ContentEmitted ? "true" : "false",
        };
        if (evt.Usage != null)
        {
            annotations["prompt_token_count"] = evt.Usage.PromptTokens.ToString(CultureInfo.InvariantCulture);
            annotations["completion_token_count"] = evt.Usage.CompletionTokens.ToString(CultureInfo.InvariantCulture);
            annotations["total_token_count"] = evt.Usage.TotalTokens.ToString(CultureInfo.InvariantCulture);
        }

        return RoleSessionSeed(
            "ai.role-session.completed",
            "ai_role_session",
            evt.SessionId,
            "Role chat session completed.",
            annotations);
    }
}

/// <summary>
/// Base for role-session committed-fact audit translators. RoleGAgent (and its
/// subclasses: WorkflowRoleGAgent, NyxIdChatGAgent, ChatbotClassifierGAgent) is
/// session/run keyed by a generated grain id (e.g. <c>nyxid-chat-{guid}</c> or a
/// workflow run/step id), never a raw external subject, so the plain base is used
/// (the origin actor id is safe to stamp un-hashed).
/// </summary>
public abstract class RoleSessionAuditTranslatorBase<TEvent> : IAuditCommittedEventTranslator
    where TEvent : class, IMessage<TEvent>, new()
{
    public abstract string EventTypeUrl { get; }

    public IReadOnlyList<AuditRecord> Translate(CommittedAuditTranslationContext context, Any eventPayload)
    {
        if (eventPayload == null || !eventPayload.Is(new TEvent().Descriptor))
            return [];

        var evt = eventPayload.Unpack<TEvent>();
        return [CommittedAuditRecordFactory.CreateSystemRecord(context, BuildSeed(context, evt))];
    }

    protected abstract CommittedAuditSeed BuildSeed(CommittedAuditTranslationContext context, TEvent evt);

    protected static CommittedAuditSeed RoleSessionSeed(
        string operationName,
        string targetKind,
        string targetId,
        string resultSummary,
        IReadOnlyDictionary<string, string>? annotations = null) =>
        new(
            operationName,
            targetKind,
            targetId,
            SensitivityLevel: AuditSensitivityLevel.Internal,
            ResultSummary: resultSummary,
            Annotations: annotations);
}
