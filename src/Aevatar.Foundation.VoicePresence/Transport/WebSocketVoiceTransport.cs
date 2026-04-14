using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Google.Protobuf;

namespace Aevatar.Foundation.VoicePresence.Transport;

/// <summary>
/// Wraps a raw <see cref="WebSocket"/> into <see cref="IVoiceTransport"/>.
/// Binary messages = PCM16 audio, text messages = JSON-encoded VoiceControlFrame.
/// </summary>
public sealed class WebSocketVoiceTransport : IVoiceTransport
{
    private const int ReceiveBufferSize = 8 * 1024;
    private readonly WebSocket _ws;
    private bool _disposed;

    public WebSocketVoiceTransport(WebSocket ws)
    {
        _ws = ws ?? throw new ArgumentNullException(nameof(ws));
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pcm16.IsEmpty) return;
        await _ws.SendAsync(pcm16, WebSocketMessageType.Binary, endOfMessage: true, ct);
    }

    public async Task SendControlAsync(VoiceControlFrame frame, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        var bytes = frame.ToByteArray();
        await _ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, endOfMessage: true, ct);
    }

    public async IAsyncEnumerable<VoiceTransportFrame> ReceiveFramesAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];

        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            int totalBytes;

            try
            {
                (result, totalBytes, buffer) = await ReceiveFullMessageAsync(buffer, ct);
            }
            catch (WebSocketException)
            {
                yield break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                yield break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                yield break;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                var audio = new byte[totalBytes];
                buffer.AsSpan(0, totalBytes).CopyTo(audio);
                yield return VoiceTransportFrame.Audio(audio);
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                var frame = TryParseControlFrame(buffer.AsSpan(0, totalBytes));
                if (frame != null)
                    yield return VoiceTransportFrame.ControlFrame(frame);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
            }
            catch
            {
                // best-effort close
            }
        }

        _ws.Dispose();
    }

    private async Task<(WebSocketReceiveResult Result, int TotalBytes, byte[] Buffer)> ReceiveFullMessageAsync(
        byte[] buffer, CancellationToken ct)
    {
        var totalBytes = 0;
        WebSocketReceiveResult result;

        do
        {
            if (totalBytes >= buffer.Length)
                Array.Resize(ref buffer, buffer.Length * 2);

            result = await _ws.ReceiveAsync(
                buffer.AsMemory(totalBytes, buffer.Length - totalBytes), ct);
            totalBytes += result.Count;
        } while (!result.EndOfMessage);

        return (result, totalBytes, buffer);
    }

    private static VoiceControlFrame? TryParseControlFrame(ReadOnlySpan<byte> utf8Bytes)
    {
        try
        {
            return VoiceControlFrame.Parser.ParseFrom(utf8Bytes.ToArray());
        }
        catch
        {
            return null;
        }
    }
}
