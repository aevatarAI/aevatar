using Aevatar.Foundation.VoicePresence;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.MiniCPM;
using Aevatar.Foundation.VoicePresence.OpenAI;

namespace Aevatar.Bootstrap.Extensions.AI;

/// <summary>
/// Bootstrap-time voice-presence module options for RoleGAgent module composition.
/// </summary>
public sealed class VoicePresenceFeatureOptions
{
    public bool EnableModuleFactory { get; set; } = true;

    public string DefaultProvider { get; set; } = "openai";

    public VoicePresenceModuleOptions Module { get; set; } = new();

    public VoiceProviderConfig OpenAIProvider { get; set; } = new()
    {
        ProviderName = "openai",
        Model = OpenAIRealtimeProviderOptions.DefaultModelName,
    };

    public VoiceSessionConfig OpenAISession { get; set; } = new()
    {
        SampleRateHz = OpenAIRealtimeProviderOptions.DefaultSampleRateHz,
    };

    public OpenAIRealtimeProviderOptions OpenAIProviderOptions { get; set; } = new();

    public VoiceProviderConfig MiniCPMProvider { get; set; } = new()
    {
        ProviderName = "minicpm",
        Model = "minicpm-o",
    };

    public VoiceSessionConfig MiniCPMSession { get; set; } = new()
    {
        SampleRateHz = MiniCPMRealtimeProviderOptions.DefaultInputSampleRateHz,
    };

    public MiniCPMRealtimeProviderOptions MiniCPMProviderOptions { get; set; } = new();
}
