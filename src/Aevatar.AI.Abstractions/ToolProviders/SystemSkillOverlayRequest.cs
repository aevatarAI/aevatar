namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// Per-turn inputs for resolving the context-aware system skill overlay. <see cref="Platform"/>
/// selects which <c>overlay-scope-*</c> members apply; <see cref="NyxIdAccessToken"/> is the per-turn
/// token used only to trigger an out-of-band background refresh of the public set (never persisted or
/// logged). Both are supplied by the reply seam, not captured from ambient state.
/// </summary>
public readonly record struct SystemSkillOverlayRequest(string? Platform, string? NyxIdAccessToken)
{
    /// <summary>Direct-chat turns have no channel; they resolve the <c>dm</c> platform (global scope only).</summary>
    public const string DirectChatPlatform = "dm";

    /// <summary>Builds a request for the direct-chat seam, which is inherently a dm turn.</summary>
    public static SystemSkillOverlayRequest DirectChat(string? nyxIdAccessToken) =>
        new(DirectChatPlatform, nyxIdAccessToken);
}
