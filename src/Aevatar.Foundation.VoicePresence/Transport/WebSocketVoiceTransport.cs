using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Google.Protobuf;

namespace Aevatar.Foundation.VoicePresence.Transport;

/// <summary>
/// Wraps a raw <see cref="WebSocket"/> into <see cref="IVoiceTransport"/>.
/// Binary messages = PCM16 audio. Text messages = JSON-encoded VoiceControlFrame.
/// </summary>
public sealed class WebSocketVoiceTransport : IVoiceTransport
{
    private const int ReceiveBufferSize = 8 * 1024;
    private static readonly JsonFormatter ControlJsonWriter = new(JsonFormatter.Settings.Default);
    private static readonly JsonParser ControlJsonReader = new(JsonParser.Settings.Default);

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
        var json = ControlJsonWriter.Format(frame);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    public async IAsyncEnumerable<VoiceTransportFrame> ReceiveFramesAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];

        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            int totalBytes;
            WebSocketMessageType messageType;
            try
            {
                (totalBytes, messageType, buffer) = await ReceiveFullMessageAsync(buffer, ct);
            }
            catch (WebSocketException)
            {
                yield break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                yield break;
            }

            if (messageType == WebSocketMessageType.Close)
                yield break;

            if (messageType == WebSocketMessageType.Binary)
            {
                var audio = new byte[totalBytes];
                buffer.AsSpan(0, totalBytes).CopyTo(audio);
                yield return VoiceTransportFrame.Audio(audio);
            }
            else if (messageType == WebSocketMessageType.Text)
            {
                var frame = TryParseControlFrame(buffer, totalBytes);
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

    private async Task<(int TotalBytes, WebSocketMessageType MessageType, byte[] Buffer)>
        ReceiveFullMessageAsync(byte[] buffer, CancellationToken ct)
    {
        var totalBytes = 0;
        ValueWebSocketReceiveResult result;

        do
        {
            if (totalBytes >= buffer.Length)
                Array.Resize(ref buffer, buffer.Length * 2);

            result = await _ws.ReceiveAsync(
                buffer.AsMemory(totalBytes, buffer.Length - totalBytes), ct);
            totalBytes += result.Count;
        } while (!result.EndOfMessage);

        return (totalBytes, result.MessageType, buffer);
    }

    private static VoiceControlFrame? TryParseControlFrame(byte[] buffer, int length)
    {
        try
        {
            var json = Encoding.UTF8.GetString(buffer, 0, length);
            return ControlJsonReader.Parse<VoiceControlFrame>(json);
        }
        catch
        {
            return null;
        }
    }
}
