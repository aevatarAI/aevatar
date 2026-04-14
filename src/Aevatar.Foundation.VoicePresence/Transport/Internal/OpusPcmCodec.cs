using Concentus;
using Concentus.Enums;

namespace Aevatar.Foundation.VoicePresence.Transport.Internal;

internal sealed class OpusPcmCodec
{
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const int MaxPacketBytes = 4000;
    private const int OpusRtpClockRate = 48000;

    private readonly IOpusEncoder _encoder;
    private readonly IOpusDecoder _decoder;
    private readonly int _pcmSampleRateHz;
    private readonly int _frameDurationMs;
    private readonly int _pcmSamplesPerFrame;
    private readonly int _pcmBytesPerFrame;
    private readonly int _maxDecodedSamples;

    public OpusPcmCodec(int pcmSampleRateHz, int frameDurationMs)
    {
        if (pcmSampleRateHz is not (8000 or 12000 or 16000 or 24000 or 48000))
        {
            throw new InvalidOperationException(
                "WebRTC voice transport supports PCM sample rates 8000/12000/16000/24000/48000 Hz only.");
        }

        if (frameDurationMs is not (10 or 20 or 40 or 60))
            throw new InvalidOperationException("WebRTC voice transport supports Opus frame durations 10/20/40/60 ms only.");

        _pcmSampleRateHz = pcmSampleRateHz;
        _frameDurationMs = frameDurationMs;
        _pcmSamplesPerFrame = pcmSampleRateHz * frameDurationMs / 1000;
        _pcmBytesPerFrame = _pcmSamplesPerFrame * (BitsPerSample / 8);
        _maxDecodedSamples = pcmSampleRateHz * 120 / 1000;
        _encoder = OpusCodecFactory.CreateEncoder(pcmSampleRateHz, Channels, OpusApplication.OPUS_APPLICATION_VOIP, null);
        _decoder = OpusCodecFactory.CreateDecoder(pcmSampleRateHz, Channels, null);
    }

    public int PcmBytesPerFrame => _pcmBytesPerFrame;

    public uint OpusRtpDurationUnitsPerFrame => (uint)(OpusRtpClockRate * _frameDurationMs / 1000);

    public byte[] EncodePacket(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length != _pcmBytesPerFrame)
        {
            throw new InvalidOperationException(
                $"Expected exactly {_pcmBytesPerFrame} PCM bytes per Opus frame but got {pcm16.Length}.");
        }

        var samples = new short[_pcmSamplesPerFrame];
        Buffer.BlockCopy(pcm16.ToArray(), 0, samples, 0, _pcmBytesPerFrame);
        var encoded = new byte[MaxPacketBytes];
        var encodedLength = _encoder.Encode(samples, _pcmSamplesPerFrame, encoded, encoded.Length);
        return encoded[..encodedLength];
    }

    public byte[] DecodePacket(ReadOnlySpan<byte> opusPacket)
    {
        var decoded = new short[_maxDecodedSamples];
        var decodedSamples = _decoder.Decode(opusPacket, decoded, _maxDecodedSamples, false);
        var pcm16 = new byte[decodedSamples * (BitsPerSample / 8)];
        Buffer.BlockCopy(decoded, 0, pcm16, 0, pcm16.Length);
        return pcm16;
    }
}
