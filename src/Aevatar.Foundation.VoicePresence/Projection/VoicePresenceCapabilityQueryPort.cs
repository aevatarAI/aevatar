using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

namespace Aevatar.Foundation.VoicePresence.Projection;

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
