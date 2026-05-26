using System.Buffers.Binary;
using System.Text;

namespace Aevatar.Foundation.VoicePresence.MiniCPM.Internal;

internal readonly record struct MiniCPMWaveAudio(byte[] Pcm16, int SampleRateHz);

internal static class MiniCPMWaveCodec
{
    public static byte[] EncodePcm16Mono(ReadOnlySpan<byte> pcm16, int sampleRateHz)
    {
        if (sampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));

        const short channelCount = 1;
        const short bitsPerSample = 16;
        const short formatTag = 1;
        var byteRate = sampleRateHz * channelCount * (bitsPerSample / 8);
        var blockAlign = (short)(channelCount * (bitsPerSample / 8));
        var riffChunkSize = 36 + pcm16.Length;

        var buffer = new byte[44 + pcm16.Length];
        WriteAscii(buffer.AsSpan(0, 4), "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), riffChunkSize);
        WriteAscii(buffer.AsSpan(8, 4), "WAVE");
        WriteAscii(buffer.AsSpan(12, 4), "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(20, 2), formatTag);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(22, 2), channelCount);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24, 4), sampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28, 4), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(32, 2), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(34, 2), bitsPerSample);
        WriteAscii(buffer.AsSpan(36, 4), "data");
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(40, 4), pcm16.Length);
        pcm16.CopyTo(buffer.AsSpan(44));
        return buffer;
    }

    public static MiniCPMWaveAudio DecodePcm16Mono(ReadOnlySpan<byte> wavBytes)
    {
        if (wavBytes.Length < 44)
            throw new InvalidDataException("MiniCPM audio payload is not a valid WAV file.");
        if (!wavBytes[..4].SequenceEqual(Encoding.ASCII.GetBytes("RIFF")) ||
            !wavBytes.Slice(8, 4).SequenceEqual(Encoding.ASCII.GetBytes("WAVE")))
        {
            throw new InvalidDataException("MiniCPM audio payload is not a RIFF/WAVE stream.");
        }

        var cursor = 12;
        int? sampleRateHz = null;
        ReadOnlySpan<byte> pcm16 = default;

        while (cursor + 8 <= wavBytes.Length)
        {
            var chunkId = Encoding.ASCII.GetString(wavBytes.Slice(cursor, 4));
            var chunkLength = BinaryPrimitives.ReadInt32LittleEndian(wavBytes.Slice(cursor + 4, 4));
            cursor += 8;

            if (chunkLength < 0 || cursor + chunkLength > wavBytes.Length)
                throw new InvalidDataException("MiniCPM audio payload contains a truncated WAV chunk.");

            var chunk = wavBytes.Slice(cursor, chunkLength);
            cursor += chunkLength;
            if ((chunkLength & 1) == 1 && cursor < wavBytes.Length)
                cursor++;

            if (chunkId == "fmt ")
            {
                if (chunk.Length < 16)
                    throw new InvalidDataException("MiniCPM audio payload has an invalid fmt chunk.");

                var formatTag = BinaryPrimitives.ReadInt16LittleEndian(chunk.Slice(0, 2));
                var channelCount = BinaryPrimitives.ReadInt16LittleEndian(chunk.Slice(2, 2));
                sampleRateHz = BinaryPrimitives.ReadInt32LittleEndian(chunk.Slice(4, 4));
                var bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(chunk.Slice(14, 2));

                if (formatTag != 1 || channelCount != 1 || bitsPerSample != 16)
                {
                    throw new InvalidDataException(
                        "MiniCPM audio payload is not mono PCM16 WAV data.");
                }
            }
            else if (chunkId == "data")
            {
                pcm16 = chunk.ToArray();
            }
        }

        if (sampleRateHz is null || pcm16.IsEmpty)
            throw new InvalidDataException("MiniCPM audio payload is missing fmt/data chunks.");

        return new MiniCPMWaveAudio(pcm16.ToArray(), sampleRateHz.Value);
    }

    private static void WriteAscii(Span<byte> buffer, string text) =>
        Encoding.ASCII.GetBytes(text, buffer);
}
