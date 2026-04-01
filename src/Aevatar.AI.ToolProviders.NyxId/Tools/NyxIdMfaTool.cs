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
        "Actions: 'status' to check if MFA is enabled, " +
        "'setup' to initiate MFA setup (returns QR code URL and secret), " +
        "'verify' to confirm MFA setup with a TOTP code.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["status", "setup", "verify"],
              "description": "Action to perform"
            },
            "code": {
              "type": "string",
              "description": "TOTP code from authenticator app (required for 'verify')"
            }
          },
          "required": ["action"]
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return "Error: No NyxID access token available. User must be authenticated.";

        string action = "status";
        string? code = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("action", out var a))
                action = a.GetString() ?? "status";
            if (doc.RootElement.TryGetProperty("code", out var c))
                code = c.GetString();
        }
        catch { /* use defaults */ }

        return action switch
        {
            "setup" => await _client.SetupMfaAsync(token, ct),
            "verify" when !string.IsNullOrWhiteSpace(code) =>
                await _client.VerifyMfaSetupAsync(token,
                    JsonSerializer.Serialize(new { code }), ct),
            "verify" => "Error: 'code' is required for verify action.",
            _ => await _client.GetCurrentUserAsync(token, ct), // mfa_enabled is in user profile
        };
    }
}
