using System.Security.Cryptography;

namespace Aevatar.GAgentService.Application.Responses;

// Refactor (iter35/cluster-037-mainnet-responses-host-orchestration):
//   Old pattern: Response/message identifiers were minted in Host endpoint locals while command orchestration was still inline.
//   New principle: Application owns opaque protocol id creation as part of normalized command state; Host treats ids as returned data.
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
