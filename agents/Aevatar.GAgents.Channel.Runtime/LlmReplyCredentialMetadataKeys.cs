using Google.Protobuf.Collections;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Per-call credentials that flow through <see cref="NeedsLlmReplyEvent.Metadata"/> from the
/// inbound channel turn runner into <see cref="ConversationGAgent"/> and onward to
/// <c>AgentRunGAgent</c> for the actual LLM call. These keys must never reach the persisted
/// state, event store, projection, or read model.
/// </summary>
/// <remarks>
/// Mirrors the string constants defined by
/// <c>Aevatar.AI.Abstractions.LLMProviders.LLMRequestMetadataKeys</c>. The constants are
/// duplicated here because <c>Aevatar.GAgents.Channel.Runtime</c> intentionally does not depend
/// on the AI abstractions package — these are wire-stable identifiers, so duplication is
/// preferable to introducing a downstream-to-upstream reference.
/// </remarks>
internal static class LlmReplyCredentialMetadataKeys
{
    public const string NyxIdAccessToken = "nyxid.access_token";
    public const string NyxIdOrgToken = "nyxid.org_token";
    public const string SenderNyxIdAccessToken = "nyxid.sender_access_token";

    public static readonly IReadOnlyList<string> All = new[]
    {
        NyxIdAccessToken,
        NyxIdOrgToken,
        SenderNyxIdAccessToken,
    };

    public static void StripFrom(MapField<string, string> metadata)
    {
        foreach (var key in All)
            metadata.Remove(key);
    }
}
