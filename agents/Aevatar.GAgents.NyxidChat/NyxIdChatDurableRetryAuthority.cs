using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatDurableRetryAuthority
{
    public static bool TrySeal(
        NyxIdChatConversationGAgentState state,
        string? suppliedOwnerSubject,
        string? requestId,
        AgentToolExecutionContextPayload? suppliedPayload,
        out AgentToolExecutionContextPayload? sealedPayload)
    {
        sealedPayload = null;
        var scopeId = Normalize(state.ScopeId);
        var actorId = Normalize(state.ConversationActorId);
        var ownerSubject = Normalize(state.OwnerSubject);
        var normalizedRequestId = Normalize(requestId);
        if (scopeId is null ||
            actorId is null ||
            ownerSubject is null ||
            normalizedRequestId is null ||
            suppliedPayload is null ||
            !string.Equals(Normalize(suppliedOwnerSubject), ownerSubject, StringComparison.Ordinal))
        {
            return false;
        }

        var supplied = AgentToolExecutionContextMapper.FromPayload(suppliedPayload);
        if (!HasCredential(supplied.Credentials) ||
            !string.Equals(supplied.Request.RequestId, normalizedRequestId, StringComparison.Ordinal) ||
            !string.Equals(supplied.Caller.ScopeId, scopeId, StringComparison.Ordinal) ||
            !string.Equals(supplied.Caller.OwnerScopeId, scopeId, StringComparison.Ordinal) ||
            !string.Equals(supplied.Caller.OwnerSubject, ownerSubject, StringComparison.Ordinal) ||
            !string.Equals(supplied.Caller.ResponseId, normalizedRequestId, StringComparison.Ordinal) ||
            !string.Equals(
                supplied.Channel.Platform,
                NyxIdChatServiceDefaults.ServiceId,
                StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(supplied.Channel.SenderId) ||
            !string.Equals(supplied.Channel.RegistrationScopeId, scopeId, StringComparison.Ordinal) ||
            !string.Equals(supplied.NyxIdAuthority.Platform, "nyxid", StringComparison.Ordinal) ||
            !string.Equals(supplied.NyxIdAuthority.ExternalUserId, ownerSubject, StringComparison.Ordinal) ||
            !string.Equals(supplied.NyxIdAuthority.Scope, "proxy", StringComparison.Ordinal) ||
            supplied.ExecutionOwner.Kind != AgentToolExecutionOwnerKind.Actor ||
            !string.Equals(supplied.ExecutionOwner.OwnerId, actorId, StringComparison.Ordinal))
        {
            return false;
        }

        sealedPayload = (AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(normalizedRequestId, null),
            Credentials = supplied.Credentials,
            Caller = new AgentToolCallerContext(
                scopeId,
                ownerSubject,
                normalizedRequestId,
                scopeId),
            Channel = new AgentToolChannelContext(
                NyxIdChatServiceDefaults.ServiceId,
                null,
                scopeId,
                null,
                null),
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "nyxid",
                string.Empty,
                ownerSubject,
                "proxy"),
            ExecutionOwner = AgentToolExecutionOwners.Actor(actorId),
        }).ToPayload();
        return true;
    }

    public static bool IsValid(
        NyxIdChatOperationKey key,
        AgentToolExecutionContextPayload? payload)
    {
        if (payload is null || string.IsNullOrWhiteSpace(key.ConversationActorId))
            return false;

        var context = AgentToolExecutionContextMapper.FromPayload(payload);
        var requestId = Normalize(context.Request.RequestId);
        var scopeId = Normalize(context.Caller.ScopeId);
        var ownerSubject = Normalize(context.Caller.OwnerSubject);
        return requestId is not null &&
               HasCredential(context.Credentials) &&
               scopeId is not null &&
               ownerSubject is not null &&
               string.Equals(context.Caller.OwnerScopeId, scopeId, StringComparison.Ordinal) &&
               string.Equals(context.Caller.ResponseId, requestId, StringComparison.Ordinal) &&
               string.Equals(
                   context.Channel.Platform,
                   NyxIdChatServiceDefaults.ServiceId,
                   StringComparison.Ordinal) &&
               string.IsNullOrWhiteSpace(context.Channel.SenderId) &&
               string.Equals(context.Channel.RegistrationScopeId, scopeId, StringComparison.Ordinal) &&
               string.Equals(context.NyxIdAuthority.Platform, "nyxid", StringComparison.Ordinal) &&
               string.Equals(context.NyxIdAuthority.ExternalUserId, ownerSubject, StringComparison.Ordinal) &&
               string.Equals(context.NyxIdAuthority.Scope, "proxy", StringComparison.Ordinal) &&
               context.ExecutionOwner.Kind == AgentToolExecutionOwnerKind.Actor &&
               string.Equals(
                   context.ExecutionOwner.OwnerId,
                   key.ConversationActorId,
                   StringComparison.Ordinal);
    }

    private static bool HasCredential(AgentToolCredentials credentials) =>
        !string.IsNullOrWhiteSpace(credentials.NyxIdAccessToken) ||
        !string.IsNullOrWhiteSpace(credentials.NyxIdOrgToken);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
