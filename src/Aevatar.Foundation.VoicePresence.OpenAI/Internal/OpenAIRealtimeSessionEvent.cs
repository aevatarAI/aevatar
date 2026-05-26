namespace Aevatar.Foundation.VoicePresence.OpenAI.Internal;

internal abstract record OpenAIRealtimeSessionEvent;

internal sealed record OpenAIRealtimeSpeechStartedEvent : OpenAIRealtimeSessionEvent;

internal sealed record OpenAIRealtimeSpeechStoppedEvent : OpenAIRealtimeSessionEvent;

internal sealed record OpenAIRealtimeResponseCreatedEvent(string ProviderResponseId) : OpenAIRealtimeSessionEvent;

internal sealed record OpenAIRealtimeResponseFinishedEvent(string ProviderResponseId, bool Cancelled)
    : OpenAIRealtimeSessionEvent;

internal sealed record OpenAIRealtimeOutputAudioDeltaEvent(string ProviderResponseId, byte[] Pcm16)
    : OpenAIRealtimeSessionEvent;

internal sealed record OpenAIRealtimeFunctionCallEvent(
    string ProviderResponseId,
    string CallId,
    string FunctionName,
    string ArgumentsJson)
    : OpenAIRealtimeSessionEvent;

internal sealed record OpenAIRealtimeErrorEvent(string Code, string Message) : OpenAIRealtimeSessionEvent;

internal sealed record OpenAIRealtimeDisconnectedEvent(string Reason) : OpenAIRealtimeSessionEvent;
