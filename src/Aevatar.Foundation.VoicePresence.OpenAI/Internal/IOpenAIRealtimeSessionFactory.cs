using Aevatar.Foundation.VoicePresence.Abstractions;

namespace Aevatar.Foundation.VoicePresence.OpenAI.Internal;

internal interface IOpenAIRealtimeSessionFactory
{
    Task<IOpenAIRealtimeSession> StartConversationSessionAsync(
        VoiceProviderConfig config,
        string defaultModel,
        CancellationToken ct);
}
