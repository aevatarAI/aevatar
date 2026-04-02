using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>Tool to manage NyxID multi-factor authentication.</summary>
public sealed class NyxIdMfaTool : IAgentTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdMfaTool(NyxIdApiClient client) => _client = client;

    public string Name => "nyxid_mfa";

    public string Description =>
        "Manage multi-factor authentication (MFA/TOTP). " +
        "Actions: status, setup, verify.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["status", "setup", "verify"],
              "description": "Action to perform (default: status)"
            },
            "code": {
              "type": "string",
              "description": "TOTP code from authenticator app (for verify)"
            }
          }
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        var args = ToolArgs.Parse(argumentsJson);
        var action = args.Str("action", "status");

        return action switch
        {
            "setup" => await _client.SetupMfaAsync(token, ct),
            "verify" => await VerifyAsync(token, args, ct),
            _ => await _client.GetCurrentUserAsync(token, ct),
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
