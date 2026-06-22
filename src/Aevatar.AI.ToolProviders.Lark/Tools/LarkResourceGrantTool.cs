using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

/// <summary>
/// Grant a member access to an EXISTING Lark resource (Base / doc / sheet). Defaults to granting the
/// current requester (channel sender) full edit access. Non-default grants (an explicit non-sender
/// member, an elevated/non-default perm, or a non-default member type) require approval, and the
/// grantee id is shape-validated so an LLM-supplied mention placeholder or display name can never be
/// used as an id.
/// </summary>
public sealed class LarkResourceGrantTool : AgentToolBase<LarkResourceGrantTool.Parameters>
{
    private static readonly HashSet<string> AllowedObjTypes =
    [
        "bitable", "docx", "sheet", "doc", "file", "wiki", "slides",
    ];

    private static readonly HashSet<string> AllowedPerms =
    [
        "view", "edit", "full_access",
    ];

    private static readonly HashSet<string> AllowedMemberTypes =
    [
        "openid", "unionid", "userid", "email", "openchat", "opendepartmentid",
    ];

    private static readonly JsonSerializerOptions ArgsOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILarkNyxClient _client;

    public LarkResourceGrantTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_resource_grant";

    public override string Description =>
        "Grant a member access to an existing Lark resource (Base / doc / sheet) that was created " +
        "earlier. Defaults to granting the current requester full edit access. Use after creating a " +
        "resource via another tool, or when the user asks to give themselves or a named person access. " +
        "The requester's id comes from the channel context (sender_id) — never from @_user_N mention text.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    /// <summary>
    /// Auto-approve only the safe default: grant the requester (channel sender) the default perm with
    /// the default member type. An explicit non-sender grantee, an elevated/non-default perm, or a
    /// non-default member type is a third-party privilege grant → require human approval.
    /// </summary>
    public override bool? RequiresApproval(string argumentsJson)
    {
        Parameters? parameters;
        try
        {
            parameters = string.IsNullOrWhiteSpace(argumentsJson)
                ? null
                : JsonSerializer.Deserialize<Parameters>(argumentsJson, ArgsOptions);
        }
        catch (JsonException)
        {
            return true;
        }

        if (parameters is null)
            return null;

        var sender = AgentToolRequestContext.ChannelSenderId?.Trim();
        var memberId = parameters.MemberId?.Trim();
        var explicitNonSender = !string.IsNullOrWhiteSpace(memberId) &&
                                !string.Equals(memberId, sender, StringComparison.Ordinal);
        var nonDefaultPerm = !string.IsNullOrWhiteSpace(parameters.Perm) &&
                             !string.Equals(parameters.Perm.Trim(), "full_access", StringComparison.OrdinalIgnoreCase);
        var nonDefaultMemberType = !string.IsNullOrWhiteSpace(parameters.MemberType) &&
                                   !string.Equals(parameters.MemberType.Trim(), "openid", StringComparison.OrdinalIgnoreCase);

        return explicitNonSender || nonDefaultPerm || nonDefaultMemberType ? true : null;
    }

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return Error("No NyxID access token available. User must be authenticated.");

        var resourceToken = parameters.Token?.Trim();
        if (string.IsNullOrWhiteSpace(resourceToken))
            return Error("token (the resource app_token / document_token) is required.");

        var objType = parameters.ObjType?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(objType) || !AllowedObjTypes.Contains(objType))
            return Error($"obj_type must be one of: {string.Join(", ", AllowedObjTypes)}");

        var memberType = string.IsNullOrWhiteSpace(parameters.MemberType)
            ? "openid"
            : parameters.MemberType.Trim().ToLowerInvariant();
        if (!AllowedMemberTypes.Contains(memberType))
            return Error($"member_type must be one of: {string.Join(", ", AllowedMemberTypes)}");

        var perm = string.IsNullOrWhiteSpace(parameters.Perm)
            ? "full_access"
            : parameters.Perm.Trim().ToLowerInvariant();
        if (!AllowedPerms.Contains(perm))
            return Error("perm must be one of: view, edit, full_access");

        var memberId = parameters.MemberId?.Trim();
        if (string.IsNullOrWhiteSpace(memberId))
            memberId = AgentToolRequestContext.ChannelSenderId?.Trim();
        if (string.IsNullOrWhiteSpace(memberId))
            return Error("No grantee: provide member_id, or rely on the channel sender_id. The requester id is the channel sender, never @_user_N from message text.");

        if (!IsValidMemberId(memberId, memberType, out var shapeError))
            return Error(shapeError);

        var response = await _client.GrantResourceMemberAsync(
            token,
            new LarkResourceMemberGrantRequest(
                Token: resourceToken,
                ObjType: objType,
                MemberId: memberId,
                MemberType: memberType,
                Perm: perm),
            ct);
        if (LarkProxyResponseParser.TryParseError(response, out var error))
            return LarkProxyResponseParser.Serialize(new { success = false, error });

        var result = LarkProxyResponseParser.ParseMemberGrantSuccess(response);
        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            token = resourceToken,
            obj_type = objType,
            member_id = result.MemberId ?? memberId,
            member_type = memberType,
            perm = result.Perm ?? perm,
        });
    }

    private static string Error(string message) =>
        LarkProxyResponseParser.Serialize(new { success = false, error = message });

    /// <summary>
    /// Reject anything that is clearly not a real id for the given member type — a mention placeholder
    /// (<c>@_user_1</c>), a display name (contains spaces), or a value whose shape contradicts the
    /// member type. This keeps LLM-supplied text from reaching the permission API as an id.
    /// </summary>
    private static bool IsValidMemberId(string memberId, string memberType, out string error)
    {
        error = string.Empty;

        if (memberId.StartsWith("@", StringComparison.Ordinal) ||
            memberId.Contains(' ', StringComparison.Ordinal))
        {
            error = $"'{memberId}' is not a valid {memberType} id (it looks like an @mention placeholder or a display name). The requester id is the channel sender_id; for someone else pass a resolved open_id (ou_...), never @_user_N.";
            return false;
        }

        switch (memberType)
        {
            case "openid":
                if (!memberId.StartsWith("ou_", StringComparison.Ordinal))
                {
                    error = $"member_type=openid requires a Lark open_id starting with 'ou_'; got '{memberId}'.";
                    return false;
                }
                break;
            case "unionid":
                if (!memberId.StartsWith("on_", StringComparison.Ordinal))
                {
                    error = $"member_type=unionid requires a union id starting with 'on_'; got '{memberId}'.";
                    return false;
                }
                break;
            case "email":
                if (!memberId.Contains('@', StringComparison.Ordinal))
                {
                    error = $"member_type=email requires an email address; got '{memberId}'.";
                    return false;
                }
                break;
        }

        return true;
    }

    public sealed class Parameters
    {
        public string? Token { get; set; }
        public string? ObjType { get; set; }
        public string? MemberId { get; set; }
        public string? MemberType { get; set; }
        public string? Perm { get; set; }
    }
}
