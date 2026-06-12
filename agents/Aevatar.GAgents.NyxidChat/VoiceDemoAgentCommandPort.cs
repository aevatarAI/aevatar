using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Voice;
using Aevatar.Foundation.VoicePresence.Abstractions;

namespace Aevatar.GAgents.NyxidChat;

public sealed class VoiceDemoAgentCommandPort
{
    private readonly IVoicePresenceCapabilityCommandPort _commandPort;

    public VoiceDemoAgentCommandPort(IVoicePresenceCapabilityCommandPort commandPort)
    {
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
    }

    public Task<VoicePresenceCapabilityAcceptedReceipt> EnableAsync(
        string actorId,
        string moduleName,
        VoiceSessionDefaults? voiceSessionDefaults = null,
        CancellationToken ct = default)
    {
        return _commandPort.EnableAsync(
            actorId,
            new VoicePresenceEnableRequested
            {
                ModuleName = moduleName ?? string.Empty,
                VoiceSessionDefaults = voiceSessionDefaults?.Clone() ?? new VoiceSessionDefaults(),
            },
            ct);
    }
}
