using System.Security.Cryptography;

namespace Aevatar.GAgentService.Application.Responses;

public static class ResponsesIds
{
    public static string NewResponseId() => "resp_" + NewOpaqueId();

    public static string NewMessageId() => "msg_" + NewOpaqueId();

    public static string NewOpaqueId()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
