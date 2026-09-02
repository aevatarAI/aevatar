using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.GAgents.ChatHistory;
using Aevatar.GAgents.UserConfig;
using Aevatar.GAgents.UserMemory;

namespace Aevatar.Studio.Projection.Audit;

// Committed-fact audit translators for per-user data actors projected under the
// StudioMaterializationContext: UserConfigGAgent (user-config-{scopeId}),
// UserMemoryGAgent (user-memory-{scopeId}), ChatConversationGAgent
// (chat-{scopeId}-{conversationId}).
//
// SUBJECT-BEARING DECISION — PLAIN for all four events.
//   Every owning actor id is keyed by the aevatar canonical scope_id, not by a
//   raw third-party external subject. scope_id is the authoritative aevatar scope
//   claim (Aevatar.Authentication.Abstractions.AevatarStandardClaimTypes.ScopeId)
//   and is recorded in plaintext everywhere else on this audit plane (see the
//   scope-keyed StudioMember / StudioTeam translators in
//   StudioLifecycleAuditTranslators.cs). The SubjectBearing base is reserved for
//   actor ids that embed a RAW external subject — e.g.
//   external-identity-binding:{platform}:{tenant}:{external_user_id}
//   (ExternalIdentityBindingAuditTranslators.cs). These are scope-keyed, so they
//   use the plain StudioAuditTranslatorBase.
//
// CONTENT EXCLUSION — no memory content, no message/conversation text, no model
// outputs, no prompts, no tokens, no credentials are ever recorded. Only
// governance-safe facts: which config setting class changed, cleared/deleted
// scope + resource kind, and the public github handle.

public sealed class UserConfigUpdatedAuditTranslator : StudioAuditTranslatorBase<UserConfigUpdatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(UserConfigUpdatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        UserConfigUpdatedEvent evt) =>
        StudioSeed(
            "user-config.updated",
            "user_config",
            context.OriginActorId,
            "",
            "User configuration updated.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // runtime_mode is a safe enum-like execution-mode selector.
                ["runtime_mode"] = evt.RuntimeMode ?? string.Empty,
                // runtime_base_url redirects execution location, so its presence is
                // governance-relevant; the raw URL itself is not recorded.
                ["has_local_runtime_base_url"] = Present(evt.LocalRuntimeBaseUrl),
                ["has_remote_runtime_base_url"] = Present(evt.RemoteRuntimeBaseUrl),
                // Model / route names are safe identifiers (cf. role_models in the
                // catalog translators); they are not secrets.
                ["default_model"] = evt.DefaultModel ?? string.Empty,
                ["preferred_llm_route"] = evt.PreferredLlmRoute ?? string.Empty,
                ["max_tool_rounds"] = evt.MaxToolRounds.ToString(),
                ["has_github_username"] = Present(evt.GithubUsername),
            });

    private static string Present(string? value) =>
        (!string.IsNullOrWhiteSpace(value)).ToString().ToLowerInvariant();
}

public sealed class UserConfigGithubUsernameUpdatedAuditTranslator
    : StudioAuditTranslatorBase<UserConfigGithubUsernameUpdatedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(UserConfigGithubUsernameUpdatedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        UserConfigGithubUsernameUpdatedEvent evt) =>
        StudioSeed(
            "user-config.github-username.updated",
            "user_config",
            context.OriginActorId,
            "",
            "User linked a GitHub identity.",
            Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // A GitHub username is a public handle, not a secret.
                ["github_username"] = evt.GithubUsername ?? string.Empty,
            });
}

public sealed class MemoryEntriesClearedAuditTranslator : StudioAuditTranslatorBase<MemoryEntriesClearedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(MemoryEntriesClearedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        MemoryEntriesClearedEvent evt) =>
        // Personal-domain destructive fact. The owning actor id (origin_actor_id
        // annotation) carries the cleared scope. MemoryEntriesClearedEvent carries
        // no count, so no count is recorded; memory content is never recorded.
        StudioSeed(
            "user-memory.cleared",
            "user_memory",
            context.OriginActorId,
            "",
            "User memory entries cleared.",
            AuditSensitivityLevel.Restricted,
            true);
}

public sealed class ConversationDeletedAuditTranslator : StudioAuditTranslatorBase<ConversationDeletedEvent>
{
    public override string EventTypeUrl =>
        AuditCommittedEventTypeUrl.FromDescriptor(ConversationDeletedEvent.Descriptor);

    protected override CommittedAuditSeed BuildSeed(
        CommittedAuditTranslationContext context,
        ConversationDeletedEvent evt) =>
        // Personal-domain destructive fact. Record the conversation id and scope
        // only; message content is never recorded.
        StudioSeed(
            "conversation.deleted",
            "chat_conversation",
            evt.ConversationId,
            evt.ScopeId,
            "Chat conversation deleted.",
            AuditSensitivityLevel.Restricted,
            true);
}
