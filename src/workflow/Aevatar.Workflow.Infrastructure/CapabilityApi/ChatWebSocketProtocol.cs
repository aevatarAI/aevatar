using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal readonly record struct ChatWebSocketInboundFrame(
    WebSocketMessageType MessageType,
    ReadOnlyMemory<byte> Payload);

internal static class ChatWebSocketProtocol
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static WebSocketMessageType NormalizeMessageType(WebSocketMessageType messageType) =>
        messageType == WebSocketMessageType.Binary
            ? WebSocketMessageType.Binary
            : WebSocketMessageType.Text;

    public static async Task<ChatWebSocketInboundFrame?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];

        while (socket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketMessageType? messageType = null;

            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;

                if (result.MessageType is not (WebSocketMessageType.Text or WebSocketMessageType.Binary))
                {
                    if (result.EndOfMessage)
                        break;

                    continue;
                }

                messageType ??= result.MessageType;
                if (result.Count > 0)
                    await ms.WriteAsync(buffer.AsMemory(0, result.Count), ct);

                if (result.EndOfMessage)
                    break;
            }

            if (messageType != null)
                return new ChatWebSocketInboundFrame(messageType.Value, ms.ToArray());
        }

        return null;
    }

    public static bool TryDecodeUtf8(ReadOnlyMemory<byte> payload, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(payload.Span);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    public static async Task SendAsync(
        WebSocket socket,
        object payload,
        CancellationToken ct,
        WebSocketMessageType messageType = WebSocketMessageType.Text)
    {
        if (socket.State != WebSocketState.Open)
            return;

        if (messageType is not (WebSocketMessageType.Text or WebSocketMessageType.Binary))
            throw new ArgumentOutOfRangeException(nameof(messageType), messageType, "Only text/binary websocket frames are supported.");

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await socket.SendAsync(bytes.AsMemory(), messageType, true, ct);
    }

    public static async Task CloseAsync(WebSocket socket, CancellationToken ct = default)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
    }
}
