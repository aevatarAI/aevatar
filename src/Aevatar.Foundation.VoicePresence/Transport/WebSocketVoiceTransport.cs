using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Google.Protobuf;

namespace Aevatar.Foundation.VoicePresence.Transport;

/// <summary>
/// Wraps a raw <see cref="WebSocket"/> into <see cref="IVoiceTransport"/>.
/// Binary messages = PCM16 audio. Text messages = JSON-encoded VoiceControlFrame.
/// </summary>
// Refactor (iter74/cluster-074-voice-ws-request-polling-close-wait):
//   Old pattern: while ws.State == Open { Task.Delay(500) } polling to keep request alive
//   New principle: Transport owns close notification; endpoint awaits completion task without periodic sleep
public sealed class WebSocketVoiceTransport : IVoiceTransport
{
    private const int ReceiveBufferSize = 8 * 1024;
    private const int MaxInputImageBytes = 500 * 1024;
    private const int MaxTextMessageBytes = ((MaxInputImageBytes + 2) / 3 * 4) + 1024;
    private static readonly JsonFormatter ControlJsonWriter = new(JsonFormatter.Settings.Default);
    private static readonly JsonParser ControlJsonReader = new(JsonParser.Settings.Default);
    private static readonly string[] SupportedInputImageMediaTypes =
    [
        "image/jpeg",
        "image/png",
    ];

    private readonly WebSocket _ws;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public WebSocketVoiceTransport(WebSocket ws)
    {
        _ws = ws ?? throw new ArgumentNullException(nameof(ws));
        if (_ws.State != WebSocketState.Open)
            _completion.TrySetResult();
    }

    public Task Completion => _completion.Task;

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pcm16.IsEmpty) return;
        try
        {
            await _ws.SendAsync(pcm16, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        catch
        {
            _completion.TrySetResult();
            throw;
        }
    }

    public async Task SendControlAsync(VoiceControlFrame frame, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        var json = ControlJsonWriter.Format(frame);
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            await _ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch
        {
            _completion.TrySetResult();
            throw;
        }
    }

    // Refactor (iter74/cluster-074-voice-ws-request-polling-close-wait):
    //   Old pattern: while ws.State == Open { Task.Delay(500) } polling to keep request alive
    //   New principle: Transport owns close notification; endpoint awaits completion task without periodic sleep
    public async IAsyncEnumerable<VoiceTransportFrame> ReceiveFramesAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];
        try
        {
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
                catch (VoiceTransportFrameRejectedException ex)
                {
                    await TryClosePolicyViolationAsync(ex.Message, ct);
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
                    var textFrame = TryParseTextFrame(buffer, totalBytes);
                    if (textFrame.RejectionReason != null)
                    {
                        await TryClosePolicyViolationAsync(textFrame.RejectionReason, ct);
                        yield break;
                    }

                    if (textFrame.Frame.HasValue)
                        yield return textFrame.Frame.Value;
                }
            }
        }
        finally
        {
            _completion.TrySetResult();
        }
    }

    // Refactor (iter74/cluster-074-voice-ws-request-polling-close-wait):
    //   Old pattern: while ws.State == Open { Task.Delay(500) } polling to keep request alive
    //   New principle: Transport owns close notification; endpoint awaits completion task without periodic sleep
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
        _completion.TrySetResult();
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

            if (result.MessageType == WebSocketMessageType.Text && totalBytes > MaxTextMessageBytes)
                throw new VoiceTransportFrameRejectedException("Voice text frame exceeds the input image size limit.");
        } while (!result.EndOfMessage);

        return (totalBytes, result.MessageType, buffer);
    }

    private static TextFrameParseResult TryParseTextFrame(byte[] buffer, int length)
    {
        var containsInputImage = ContainsInputImageProperty(buffer, length);
        try
        {
            var json = Encoding.UTF8.GetString(buffer, 0, length);
            var frame = ControlJsonReader.Parse<VoiceControlFrame>(json);
            if (frame.FrameCase == VoiceControlFrame.FrameOneofCase.InputImage)
            {
                var rejection = ValidateInputImage(frame.InputImage);
                return rejection == null
                    ? TextFrameParseResult.Accepted(VoiceTransportFrame.InputImageFrame(frame.InputImage.Clone()))
                    : TextFrameParseResult.Rejected(rejection);
            }

            return TextFrameParseResult.Accepted(VoiceTransportFrame.ControlFrame(frame));
        }
        catch
        {
            return containsInputImage
                ? TextFrameParseResult.Rejected("Invalid voice input image frame.")
                : TextFrameParseResult.Ignored();
        }
    }

    private static bool ContainsInputImageProperty(byte[] buffer, int length)
    {
        try
        {
            var reader = new Utf8JsonReader(buffer.AsSpan(0, length));
            if (!JsonDocument.TryParseValue(ref reader, out var document))
                return false;

            using (document)
            {
                return document.RootElement.ValueKind == JsonValueKind.Object &&
                       (document.RootElement.TryGetProperty("inputImage", out _) ||
                        document.RootElement.TryGetProperty("input_image", out _));
            }
        }
        catch
        {
            return false;
        }
    }

    private static string? ValidateInputImage(VoiceInputImage? inputImage)
    {
        if (inputImage == null ||
            inputImage.Data.IsEmpty)
        {
            return "Voice input image data is required.";
        }

        var mediaType = inputImage.MediaType.Trim();
        if (!SupportedInputImageMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
            return "Voice input image media type must be image/jpeg or image/png.";

        return inputImage.Data.Length <= MaxInputImageBytes
            ? null
            : "Voice input image exceeds the 500 KB size limit.";
    }

    private async Task TryClosePolicyViolationAsync(string reason, CancellationToken ct)
    {
        if (_ws.State is not WebSocketState.Open and not WebSocketState.CloseReceived)
            return;

        try
        {
            await _ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, ct);
        }
        catch
        {
            // best-effort close for invalid client input
        }
    }

    private readonly record struct TextFrameParseResult(VoiceTransportFrame? Frame, string? RejectionReason)
    {
        public static TextFrameParseResult Accepted(VoiceTransportFrame frame) => new(frame, null);

        public static TextFrameParseResult Ignored() => new(null, null);

        public static TextFrameParseResult Rejected(string reason) => new(null, reason);
    }

    private sealed class VoiceTransportFrameRejectedException(string message) : Exception(message);
}
