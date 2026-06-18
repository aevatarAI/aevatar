using Aevatar.Foundation.VoicePresence.OpenAI;

namespace Aevatar.Bootstrap.Extensions.AI;

/// <summary>
/// Options for brokering the OpenAI Realtime provider credential through NyxID.
/// Bound from <c>Aevatar:VoicePresence:OpenAI:Nyxid:*</c>. When
/// <see cref="ServiceSlug" /> is set, broker mode is enabled: the long-lived
/// OpenAI key lives only in the NyxID service and aevatar mints a short-lived
/// ephemeral per session, so no static <c>OPENAI_API_KEY</c> is required. See ADR-0033.
/// </summary>
public sealed class NyxIdRealtimeProviderCredentialOptions
{
    /// <summary>
    /// NyxID service slug that holds the real OpenAI key (e.g. <c>openai-realtime</c>).
    /// Empty disables broker mode (static-key / local-dev path stays in effect).
    /// </summary>
    public string ServiceSlug { get; set; } = string.Empty;

    /// <summary>
    /// Proxied OpenAI path that mints the realtime ephemeral client secret. The
    /// NyxID service endpoint must be the bare host (<c>https://api.openai.com</c>)
    /// so this resolves to <c>…/v1/realtime/client_secrets</c>.
    /// </summary>
    public string MintPath { get; set; } = "v1/realtime/client_secrets";

    /// <summary>Model used when minting the ephemeral session; falls back to the provider config model.</summary>
    public string Model { get; set; } = OpenAIRealtimeProviderOptions.DefaultModelName;

    public bool Enabled => !string.IsNullOrWhiteSpace(ServiceSlug);
}
