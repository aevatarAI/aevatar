using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

/// <summary>
/// Create a Lark Bitable Base on the requester's behalf and deterministically grant the requester
/// (the channel sender) full edit access in the same operation, so the grant is never left to a
/// separate LLM step. Falls back to a tenant-public link only when the requester grant cannot be
/// applied (grant error or no resolvable sender), and reports that fallback loudly.
/// </summary>
public sealed class LarkBaseCreateTool : AgentToolBase<LarkBaseCreateTool.Parameters>
{
    private readonly ILarkNyxClient _client;

    public LarkBaseCreateTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_base_create";

    public override string Description =>
        "Create a Lark Bitable Base (多维表格) for the requester through the Nyx-backed bot, and " +
        "automatically grant the requester full edit access to the new Base. Use this whenever a user " +
        "asks you to create a 多维表格 / Bitable / multi-dimensional table — prefer it over raw API " +
        "calls, because it grants the requester access for you and returns the Base link.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var name = parameters.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "name is required." });
        if (name.Length > 255)
            return LarkProxyResponseParser.Serialize(new { success = false, error = "name exceeds the maximum of 255 characters." });

        var createResponse = await _client.CreateBitableAppAsync(
            token,
            new LarkBitableCreateRequest(name, parameters.FolderToken?.Trim()),
            ct);
        if (LarkProxyResponseParser.TryParseError(createResponse, out var createError))
            return LarkProxyResponseParser.Serialize(new { success = false, error = createError });

        var createResult = LarkProxyResponseParser.ParseBitableCreateSuccess(createResponse);
        if (string.IsNullOrWhiteSpace(createResult.AppToken))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "bitable_create_missing_token" });

        // The requester's id is the channel sender — typed, server-known, never @_user_N message text.
        var sender = AgentToolRequestContext.ChannelSenderId?.Trim();
        if (string.IsNullOrWhiteSpace(sender))
            return await FallbackToPublicAsync(token, createResult, reason: "no_sender", grantError: null, ct);

        var grantResponse = await _client.GrantResourceMemberAsync(
            token,
            new LarkResourceMemberGrantRequest(
                Token: createResult.AppToken,
                ObjType: "bitable",
                MemberId: sender,
                MemberType: "openid",
                Perm: "full_access"),
            ct);
        if (LarkProxyResponseParser.TryParseError(grantResponse, out var grantError))
            return await FallbackToPublicAsync(token, createResult, reason: null, grantError: grantError, ct);

        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            app_token = createResult.AppToken,
            url = createResult.Url,
            default_table_id = createResult.DefaultTableId,
            granted = true,
            grantee = sender,
            visibility = "requester_full_access",
        });
    }

    private async Task<string> FallbackToPublicAsync(
        string token,
        LarkBitableCreateResult createResult,
        string? reason,
        string? grantError,
        CancellationToken ct)
    {
        // Decided fallback (per brief): when the requester cannot be granted directly, make the Base
        // tenant-visible so the user can at least open it — surfaced LOUDLY so the bot tells the user
        // and can offer to restrict it. Never a silent privacy downgrade.
        var publicResponse = await _client.SetDrivePermissionAsync(
            token,
            new LarkDrivePermissionRequest(
                DocumentToken: createResult.AppToken!,
                Visibility: LarkDocxVisibility.Editable,
                ReceiveId: null,
                ReceiveIdType: null,
                ObjType: "bitable"),
            ct);
        var publicFailed = LarkProxyResponseParser.TryParseError(publicResponse, out var publicError);
        return LarkProxyResponseParser.Serialize(new
        {
            success = !publicFailed,
            app_token = createResult.AppToken,
            url = createResult.Url,
            default_table_id = createResult.DefaultTableId,
            granted = false,
            fallback_to_public = !publicFailed,
            reason,
            grant_error = grantError,
            public_error = publicFailed ? publicError : null,
            note = publicFailed
                ? "Base created but could not be shared automatically; the requester may need to be granted access manually."
                : "Could not grant the requester directly, so the Base was made org-visible instead — offer to restrict it to the requester.",
        });
    }

    public sealed class Parameters
    {
        public string? Name { get; set; }
        public string? FolderToken { get; set; }
    }
}
