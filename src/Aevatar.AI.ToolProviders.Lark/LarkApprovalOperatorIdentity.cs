using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark;

/// <summary>
/// The Lark identity (user id + matching user_id_type) on whose behalf approval APIs are called.
/// Always resolved server-side from the current turn's channel context, never from model-supplied
/// tool arguments: Lark approval endpoints trust the caller-reported user_id under the org-shared
/// tenant credential, so a model-controlled value would let any caller list or act on anyone's
/// approval tasks.
/// </summary>
public sealed record LarkApprovalOperatorIdentity(string UserId, string UserIdType)
{
    private const string OperatorUnionIdKey = "channel.lark.operator_union_id";
    private const string OperatorUserIdKey = "channel.lark.operator_user_id";
    private const string OperatorOpenIdKey = "channel.lark.operator_open_id";
    private const string SenderUnionIdKey = "channel.lark.union_id";
    private const string LarkPlatform = "lark";

    /// <summary>
    /// Resolution favors tenant-stable, cross-app-safe ids: the card-action operator
    /// (union_id, then user_id, then open_id), falling back to the inbound chat sender
    /// (union_id, then open_id). The sender open_id is only trusted when the turn's channel
    /// platform is Lark because <see cref="AgentToolRequestContext.ChannelSenderId"/> is
    /// platform-agnostic. Returns null when the turn carries no Lark identity.
    /// </summary>
    public static LarkApprovalOperatorIdentity? Resolve()
    {
        var operatorUnionId = Normalize(AgentToolRequestContext.TryGetExternalMetadata(OperatorUnionIdKey));
        if (operatorUnionId != null)
            return new LarkApprovalOperatorIdentity(operatorUnionId, "union_id");

        var operatorUserId = Normalize(AgentToolRequestContext.TryGetExternalMetadata(OperatorUserIdKey));
        if (operatorUserId != null)
            return new LarkApprovalOperatorIdentity(operatorUserId, "user_id");

        var operatorOpenId = Normalize(AgentToolRequestContext.TryGetExternalMetadata(OperatorOpenIdKey));
        if (operatorOpenId != null)
            return new LarkApprovalOperatorIdentity(operatorOpenId, "open_id");

        var senderUnionId = Normalize(AgentToolRequestContext.TryGetExternalMetadata(SenderUnionIdKey));
        if (senderUnionId != null)
            return new LarkApprovalOperatorIdentity(senderUnionId, "union_id");

        if (string.Equals(Normalize(AgentToolRequestContext.ChannelPlatform), LarkPlatform, StringComparison.OrdinalIgnoreCase))
        {
            var senderOpenId = Normalize(AgentToolRequestContext.ChannelSenderId);
            if (senderOpenId != null)
                return new LarkApprovalOperatorIdentity(senderOpenId, "open_id");
        }

        return null;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
