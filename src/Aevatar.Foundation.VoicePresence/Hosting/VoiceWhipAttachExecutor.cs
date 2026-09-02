using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed class VoiceWhipAttachExecutor
{
    private readonly IVoiceVolatileMediaStreamPort _mediaStreamPort;
    private readonly IWebRtcVoiceTransportFactory _transportFactory;
    private readonly ILogger<VoiceWhipAttachExecutor>? _logger;

    public VoiceWhipAttachExecutor(
        IVoiceVolatileMediaStreamPort mediaStreamPort,
        IWebRtcVoiceTransportFactory transportFactory,
        ILogger<VoiceWhipAttachExecutor>? logger = null)
    {
        _mediaStreamPort = mediaStreamPort ?? throw new ArgumentNullException(nameof(mediaStreamPort));
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _logger = logger;
    }

    public async Task<VoiceWhipAttachResult> AttachAsync(
        HttpContext http,
        VoiceRealtimeSessionAccepted accepted,
        string offerSdp,
        string resourceLocation,
        VoiceToolCredentialTransportBinding? transportBinding = null,
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
                _logger,
                ct);
            var lifetimeCompleted = await _mediaStreamPort.AttachAsync(
                accepted.LeaseHandle,
                transportSession.Transport,
                transportBinding,
                ct);
            attached = true;
            _ = ObserveTransportLifetimeAsync(
                _mediaStreamPort,
                accepted.LeaseHandle,
                lifetimeCompleted,
                realtimeSubscription,
                transportSession.Completion,
                _logger);

            return new VoiceWhipAttachResult(transportSession.AnswerSdp, resourceLocation);
        }
        catch (VoiceTransportAlreadyAttachedException ex) when (!attached)
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
        Task completion,
        ILogger<VoiceWhipAttachExecutor>? logger)
    {
        try
        {
            await completion;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Voice WHIP transport completion failed during best-effort cleanup.");
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
