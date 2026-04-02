using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Tool for managing relay pairing directly via the in-process PairingStore.
/// Registered as a standalone IAgentToolSource from the NyxIdChat module.
/// </summary>
public sealed class NyxIdPairingTool : IAgentTool
{
    private readonly NyxIdRelayPairingStore _store;

    public NyxIdPairingTool(NyxIdRelayPairingStore store) => _store = store;

    public string Name => "nyxid_pairing";

    public string Description =>
        "Manage Telegram/channel bot pairing. When a new user messages the bot, " +
        "they receive a pairing code. Use this tool to list pending codes, approve them, " +
        "list paired users, or unpair. " +
        "Actions: pending, approve, paired, unpair.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["pending", "approve", "paired", "unpair"],
              "description": "Action (default: pending)"
            },
            "scope_id": {
              "type": "string",
              "description": "User's NyxID user ID. Get from nyxid_account."
            },
            "code": {
              "type": "string",
              "description": "Pairing code to approve (e.g. PAIR-a1b2c3d4)"
            },
            "platform": {
              "type": "string",
              "description": "Platform (for unpair)"
            },
            "sender_platform_id": {
              "type": "string",
              "description": "Sender's platform ID (for unpair)"
            }
          }
        }
        """;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var args = NyxIdPairingToolArgs.Parse(argumentsJson);
        var action = args.Action;
        var scopeId = args.ScopeId;

        if (string.IsNullOrWhiteSpace(scopeId))
            return """{"error":"'scope_id' is required. Use nyxid_account to get the user's ID."}""";

        return action switch
        {
            "approve" => await ApproveAsync(args, ct),
            "paired" => await ListPairedAsync(scopeId, ct),
            "unpair" => await UnpairAsync(args, ct),
            _ => ListPending(scopeId),
        };
    }

    private string ListPending(string scopeId)
    {
        var pending = _store.ListPending(scopeId);
        return JsonSerializer.Serialize(new { pending }, JsonOptions);
    }

    private async Task<string> ApproveAsync(NyxIdPairingToolArgs args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Code))
            return """{"error":"'code' is required for approve"}""";

        var request = await _store.ApprovePairingAsync(args.Code, ct);
        if (request == null)
            return """{"error":"Pairing code not found or expired"}""";


        return JsonSerializer.Serialize(new
        {
            status = "paired",
            platform = request.Platform,
            sender_platform_id = request.SenderPlatformId,
            sender_display_name = request.SenderDisplayName,
        }, JsonOptions);
    }

    private async Task<string> ListPairedAsync(string scopeId, CancellationToken ct)
    {
        var paired = await _store.ListPairedAsync(scopeId, ct);
        return JsonSerializer.Serialize(new { paired }, JsonOptions);
    }

    private async Task<string> UnpairAsync(NyxIdPairingToolArgs args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Platform) || string.IsNullOrWhiteSpace(args.SenderPlatformId))
            return """{"error":"'platform' and 'sender_platform_id' required for unpair"}""";

        var removed = await _store.UnpairAsync(args.ScopeId!, args.Platform, args.SenderPlatformId, ct);
        return removed
            ? """{"status":"unpaired"}"""
            : """{"error":"Sender not found"}""";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed class NyxIdPairingToolArgs
    {
        public string Action { get; init; } = "pending";
        public string? ScopeId { get; init; }
        public string? Code { get; init; }
        public string? Platform { get; init; }
        public string? SenderPlatformId { get; init; }

        public static NyxIdPairingToolArgs Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                return new NyxIdPairingToolArgs
                {
                    Action = TryStr(root, "action") ?? "pending",
                    ScopeId = TryStr(root, "scope_id"),
                    Code = TryStr(root, "code"),
                    Platform = TryStr(root, "platform"),
                    SenderPlatformId = TryStr(root, "sender_platform_id"),
                };
            }
            catch { return new(); }
        }

        private static string? TryStr(System.Text.Json.JsonElement el, string name)
        {
            if (el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                return v.GetString();
            // Case-insensitive fallback
            foreach (var prop in el.EnumerateObject())
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    return prop.Value.GetString();
            return null;
        }
    }
}

/// <summary>Registers NyxIdPairingTool as an IAgentToolSource.</summary>
public sealed class NyxIdPairingToolSource : IAgentToolSource
{
    private readonly NyxIdRelayPairingStore _store;

    public NyxIdPairingToolSource(NyxIdRelayPairingStore store) => _store = store;

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([new NyxIdPairingTool(_store)]);
}
