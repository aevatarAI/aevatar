using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Transport;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.VoicePresence.Projection;

internal static class VoicePresenceCapabilityReadModelMapper
{
    public const string DefaultModuleName = "voice_presence";

    public static string BuildId(string actorId, string moduleName) =>
        $"{actorId.Trim()}:{NormalizeModuleName(moduleName)}";

    public static VoicePresenceCapabilityReadModel FromRuntimeState(
        string actorId,
        string moduleName,
        VoicePresenceRuntimeState state,
        long stateVersion,
        string lastEventId,
        DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentNullException.ThrowIfNull(state);

        var normalizedModuleName = NormalizeModuleName(moduleName);
        var updatedAtUtc = updatedAt.ToUniversalTime();
        return new VoicePresenceCapabilityReadModel
        {
            Id = BuildId(actorId, normalizedModuleName),
            ActorId = actorId.Trim(),
            ModuleName = normalizedModuleName,
            StateVersion = stateVersion,
            LastEventId = lastEventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAtUtc),
            Initialized = state.Initialized,
            TransportAttached = state.TransportAttached,
            PcmSampleRateHz = state.PcmSampleRateHz > 0
                ? state.PcmSampleRateHz
                : WebRtcVoiceTransportOptions.DefaultPcmSampleRateHz,
            ActiveSessionId = state.ActiveSessionId ?? string.Empty,
            LeaseExpiresAt = state.LeaseExpiresAt?.Clone(),
            RemoteAudioSupport = state.RemoteAudioSupport == VoiceRemoteAudioSupport.Unspecified
                ? VoiceRemoteAudioSupport.LocalOnly
                : state.RemoteAudioSupport,
            ActiveTransportLeaseId = state.ActiveTransportLeaseId ?? string.Empty,
            LeaseEpoch = state.LeaseEpoch,
            ActiveLeaseOwnerId = state.ActiveLeaseOwnerId ?? string.Empty,
        };
    }

    public static VoicePresenceCapabilitySnapshot ToSnapshot(VoicePresenceCapabilityReadModel readModel)
    {
        ArgumentNullException.ThrowIfNull(readModel);

        return new VoicePresenceCapabilitySnapshot(
            readModel.ActorId,
            readModel.ModuleName,
            readModel.StateVersion,
            readModel.LastEventId,
            readModel.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            readModel.Initialized,
            readModel.TransportAttached,
            readModel.PcmSampleRateHz > 0
                ? readModel.PcmSampleRateHz
                : WebRtcVoiceTransportOptions.DefaultPcmSampleRateHz,
            string.IsNullOrWhiteSpace(readModel.ActiveSessionId) ? null : readModel.ActiveSessionId,
            readModel.LeaseExpiresAt?.ToDateTimeOffset(),
            readModel.RemoteAudioSupport == VoiceRemoteAudioSupport.Unspecified
                ? VoiceRemoteAudioSupport.LocalOnly
                : readModel.RemoteAudioSupport,
            string.IsNullOrWhiteSpace(readModel.ActiveTransportLeaseId) ? null : readModel.ActiveTransportLeaseId,
            readModel.LeaseEpoch,
            string.IsNullOrWhiteSpace(readModel.ActiveLeaseOwnerId) ? null : readModel.ActiveLeaseOwnerId);
    }

    public static string NormalizeModuleName(string? moduleName) =>
        string.IsNullOrWhiteSpace(moduleName)
            ? DefaultModuleName
            : moduleName.Trim();
}
