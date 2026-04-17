using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace Aevatar.Foundation.VoicePresence.Transport.Internal;

internal sealed class SipsorceryWebRtcVoicePeer : IWebRtcVoicePeer
{
    private readonly RTCPeerConnection _peerConnection;
    private readonly WebRtcVoiceTransportOptions _options;
    private RTCDataChannel? _controlChannel;
    private bool _closedRaised;

    private SipsorceryWebRtcVoicePeer(
        RTCPeerConnection peerConnection,
        RTCDataChannel? controlChannel,
        WebRtcVoiceTransportOptions options,
        string answerSdp)
    {
        _peerConnection = peerConnection;
        _controlChannel = controlChannel;
        _options = options;
        AnswerSdp = answerSdp;

        _peerConnection.OnRtpPacketReceived += OnRtpPacketReceived;
        _peerConnection.ondatachannel += OnDataChannel;
        _peerConnection.onconnectionstatechange += _ => TryRaiseClosed();
        _peerConnection.oniceconnectionstatechange += _ => TryRaiseClosed();

        WireControlChannel(_controlChannel);
    }

    public string AnswerSdp { get; }

    public event Action<ReadOnlyMemory<byte>>? OnAudioPacketReceived;
    public event Action<string>? OnControlMessageReceived;
    public event Action? OnClosed;

    public static async Task<SipsorceryWebRtcVoicePeer> CreateAsync(
        string remoteOfferSdp,
        WebRtcVoiceTransportOptions options,
        CancellationToken ct)
    {
        var peerConnection = new RTCPeerConnection(new RTCConfiguration());
        RTCDataChannel? controlChannel = null;
        try
        {
            var localAudioTrack = new MediaStreamTrack(
                new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 1, null),
                MediaStreamStatusEnum.SendRecv);
            peerConnection.addTrack(localAudioTrack);
            controlChannel = await peerConnection.createDataChannel(options.ControlDataChannelLabel);

            var remoteDescription = new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = remoteOfferSdp,
            };
            var setResult = peerConnection.setRemoteDescription(remoteDescription);
            if (setResult != SetDescriptionResultEnum.OK)
            {
                throw new InvalidOperationException($"Failed to set WebRTC remote offer: {setResult}.");
            }

            var answer = peerConnection.createAnswer(null);
            await peerConnection.setLocalDescription(answer);
            await WaitForIceGatheringAsync(peerConnection, options.IceGatheringTimeout, ct);

            var answerSdp = peerConnection.currentLocalDescription?.sdp?.ToString();
            if (string.IsNullOrWhiteSpace(answerSdp))
                throw new InvalidOperationException("WebRTC local SDP answer was not generated.");

            return new SipsorceryWebRtcVoicePeer(peerConnection, controlChannel, options, answerSdp);
        }
        catch
        {
            controlChannel?.close();
            peerConnection.close();
            peerConnection.Dispose();
            throw;
        }
    }

    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> encodedFrame, uint durationRtpUnits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _peerConnection.SendAudio(durationRtpUnits, encodedFrame.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask SendControlAsync(string controlJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_controlChannel == null)
            throw new InvalidOperationException("WebRTC control data channel is not available.");

        _controlChannel.send(controlJson);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _controlChannel?.close();
        _peerConnection.close();
        _peerConnection.Dispose();
        TryRaiseClosed();
        return ValueTask.CompletedTask;
    }

    private void OnRtpPacketReceived(System.Net.IPEndPoint _, SDPMediaTypesEnum mediaType, RTPPacket packet)
    {
        if (mediaType != SDPMediaTypesEnum.audio || packet.Payload == null || packet.Payload.Length == 0)
            return;

        OnAudioPacketReceived?.Invoke(packet.Payload);
    }

    private void OnDataChannel(RTCDataChannel channel)
    {
        if (!string.Equals(channel.label, _options.ControlDataChannelLabel, StringComparison.Ordinal))
            return;

        _controlChannel = channel;
        WireControlChannel(channel);
    }

    private void WireControlChannel(RTCDataChannel? channel)
    {
        if (channel == null)
            return;

        channel.onmessage += (_, protocol, data) =>
        {
            if (protocol is not (
                DataChannelPayloadProtocols.WebRTC_String or
                DataChannelPayloadProtocols.WebRTC_Binary or
                DataChannelPayloadProtocols.WebRTC_String_Empty or
                DataChannelPayloadProtocols.WebRTC_Binary_Empty))
            {
                return;
            }

            var text = System.Text.Encoding.UTF8.GetString(data);
            OnControlMessageReceived?.Invoke(text);
        };
    }

    private static async Task WaitForIceGatheringAsync(
        RTCPeerConnection peerConnection,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero || peerConnection.iceGatheringState == RTCIceGatheringState.complete)
            return;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void HandleState(RTCIceGatheringState state)
        {
            if (state == RTCIceGatheringState.complete)
                completion.TrySetResult();
        }

        peerConnection.onicegatheringstatechange += HandleState;
        try
        {
            if (peerConnection.iceGatheringState == RTCIceGatheringState.complete)
                return;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            await completion.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // best effort: return the partial candidate set when gathering times out
        }
        finally
        {
            peerConnection.onicegatheringstatechange -= HandleState;
        }
    }

    private void TryRaiseClosed()
    {
        if (_closedRaised)
            return;

        if (_peerConnection.connectionState is not (RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected) &&
            _peerConnection.iceConnectionState is not (RTCIceConnectionState.closed or RTCIceConnectionState.failed or RTCIceConnectionState.disconnected))
        {
            return;
        }

        _closedRaised = true;
        OnClosed?.Invoke();
    }
}
