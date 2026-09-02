using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID multi-factor authentication.</summary>
public sealed class NyxIdMfaTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private static readonly NyxIdClosedActionParser<NyxIdMfaAction> ActionParser = new(
    [
        new("status", NyxIdMfaAction.Status, new(false, true, false)),
        new("setup", NyxIdMfaAction.Setup, new(true, false, false)),
        new("verify", NyxIdMfaAction.Verify, new(true, false, false)),
    ], "status");

    public IReadOnlyCollection<string> Capabilities => NyxIdToolSurfaces.HumanSessionOnly;

    private readonly NyxIdApiClient _client;

    public NyxIdMfaTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_mfa";

    public string Description =>
        "Manage multi-factor authentication (MFA/TOTP). " +
        "Actions: status, setup, verify.";

    public string ParametersSchema => $$"""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": {{ActionParser.ActionNamesJson}},
              "description": "Action to perform (default: status)"
            },
            "code": {
              "type": "string",
              "description": "TOTP code from authenticator app (for verify)"
            }
          }
        }
        """;

    public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    public AgentToolCallSafety GetCallSafety(string argumentsJson) =>
        ActionParser.Classify(argumentsJson);

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var parsed = ActionParser.Parse(argumentsJson);
        if (!parsed.IsValid)
            return NyxIdClosedActionParser<NyxIdMfaAction>.InvalidActionJson;

        var args = ToolArgs.Parse(argumentsJson);
        return parsed.Action switch
        {
            NyxIdMfaAction.Setup => await _client.SetupMfaAsync(token, ct),
            NyxIdMfaAction.Verify => await VerifyAsync(token, args, ct),
            NyxIdMfaAction.Status => await _client.GetCurrentUserAsync(token, ct),
            _ => NyxIdClosedActionParser<NyxIdMfaAction>.InvalidActionJson,
        };
    }

    private async Task<string> VerifyAsync(string token, ToolArgs args, CancellationToken ct)
    {
        var code = args.Str("code");
        if (string.IsNullOrWhiteSpace(code))
            return """{"error":"'code' is required for verify"}""";
        return await _client.VerifyMfaSetupAsync(token, JsonSerializer.Serialize(new { code }), ct);
    }
}

internal enum NyxIdMfaAction
{
    Status,
    Setup,
    Verify,
}
