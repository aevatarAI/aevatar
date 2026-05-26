using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

namespace Aevatar.Foundation.VoicePresence.Projection;

// Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
//   Old pattern: InProcessActorVoicePresenceSessionResolver 通过 runtime instance shape 判定 voice session capability(违反"运行时形态不是业务事实")。
//   New principle: voice capability/session facts 由 actor-owned VoicePresenceCapabilityReadModel 暴露;host resolver 只 obtain lease/session handle;走 existing typed lease command/event flow,no runtime-shape inspection。
public sealed class VoicePresenceCapabilityQueryPort : IVoicePresenceCapabilityQueryPort
{
    private readonly IProjectionDocumentReader<VoicePresenceCapabilityReadModel, string> _reader;

    public VoicePresenceCapabilityQueryPort(
        IProjectionDocumentReader<VoicePresenceCapabilityReadModel, string> reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<VoicePresenceCapabilitySnapshot?> GetAsync(
        string actorId,
        string? moduleName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        var normalizedModuleName = VoicePresenceCapabilityReadModelMapper.NormalizeModuleName(moduleName);
        var readModel = await _reader.GetAsync(
            VoicePresenceCapabilityReadModelMapper.BuildId(actorId, normalizedModuleName),
            ct);

        return readModel == null
            ? null
            : VoicePresenceCapabilityReadModelMapper.ToSnapshot(readModel);
    }
}
