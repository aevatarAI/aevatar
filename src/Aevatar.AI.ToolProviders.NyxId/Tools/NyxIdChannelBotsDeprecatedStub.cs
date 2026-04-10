using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>
/// Stub that replaces the deprecated NyxIdChannelBotsTool.
/// Keeps the same tool name so existing actors with cached system prompts
/// that reference nyxid_channel_bots won't hit a missing-tool error.
/// Every call returns a redirect message pointing to channel_registrations.
/// </summary>
public sealed class NyxIdChannelBotsDeprecatedStub : IAgentTool
{
    public string Name => "nyxid_channel_bots";

    public string Description =>
        "DEPRECATED — use channel_registrations instead. " +
        "This tool no longer functions. All channel bot management is now handled by the channel_registrations tool.";

    public string ParametersSchema => """{"type":"object","properties":{"action":{"type":"string"}}}""";

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
        Task.FromResult("""{"error":"nyxid_channel_bots is deprecated and no longer works. Use the channel_registrations tool instead. Example: channel_registrations action=register platform=lark nyx_provider_slug=api-lark-bot"}""");
}
