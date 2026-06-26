using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Transport;
using Aevatar.Foundation.VoicePresence.Transport.Internal;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed class VoiceWhipAttachExecutor
{
    private readonly IVoiceVolatileMediaStreamPort _mediaStreamPort;
    private readonly IWebRtcVoiceTransportFactory _transportFactory;

    public VoiceWhipAttachExecutor(
        IVoiceVolatileMediaStreamPort mediaStreamPort,
        IWebRtcVoiceTransportFactory? transportFactory = null)
    {
        _mediaStreamPort = mediaStreamPort ?? throw new ArgumentNullException(nameof(mediaStreamPort));
        _transportFactory = transportFactory ?? new SipsorceryWebRtcVoiceTransportFactory();
    }

    public async Task<VoiceWhipAttachResult> AttachAsync(
        HttpContext http,
        VoiceRealtimeSessionAccepted accepted,
        string offerSdp,
        string resourceLocation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentException.ThrowIfNullOrWhiteSpace(offerSdp);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceLocation);

        var transportSession = await _transportFactory.CreateAsync(
            offerSdp,
            new WebRtcVoiceTransportOptions
            {
                PcmSampleRateHz = accepted.PcmSampleRateHz,
                ControlDataChannelLabel = "vp-control",
            },
            ct);

        var attached = false;
        IAsyncDisposable? realtimeSubscription = null;
        try
        {
            realtimeSubscription = await VoiceRealtimeTransportControlBridge.SubscribeAsync(
                http.RequestServices,
                accepted,
                transportSession.Transport,
                ct);
            var lifetimeCompleted = await _mediaStreamPort.AttachAsync(
                accepted.LeaseHandle,
                transportSession.Transport,
                ct);
            attached = true;
            _ = ObserveTransportLifetimeAsync(
                _mediaStreamPort,
                accepted.LeaseHandle,
                lifetimeCompleted,
                realtimeSubscription,
                transportSession.Completion);

            return new VoiceWhipAttachResult(transportSession.AnswerSdp, resourceLocation);
        }
        catch (InvalidOperationException ex) when (!attached)
        {
            if (realtimeSubscription != null)
                await realtimeSubscription.DisposeAsync();

            await transportSession.Transport.DisposeAsync();
            throw new VoiceWhipTransportAttachConflictException(ex);
        }
        catch
        {
            if (realtimeSubscription != null)
                await realtimeSubscription.DisposeAsync();

            if (!attached)
                await transportSession.Transport.DisposeAsync();

            throw;
        }
    }

    private static async Task ObserveTransportLifetimeAsync(
        IVoiceVolatileMediaStreamPort mediaStreamPort,
        VoicePresenceSessionLeaseHandle handle,
        VoiceTransportLifetimeCompleted? lifetimeCompleted,
        IAsyncDisposable realtimeSubscription,
        Task completion)
    {
        try
        {
            await completion;
        }
        catch
        {
            // transport completion is best-effort cleanup only
        }
        finally
        {
            await realtimeSubscription.DisposeAsync();
            await mediaStreamPort.CompleteTransportLifetimeAsync(
                handle,
                lifetimeCompleted,
                "host_transport_completed");
        }
    }
}

public sealed record VoiceWhipAttachResult(string AnswerSdp, string ResourceLocation);

public sealed class VoiceWhipTransportAttachConflictException(Exception innerException)
    : Exception("Voice transport already attached.", innerException);
