using System.Buffers.Binary;
using System.Text;

namespace Aevatar.GAgents.Scheduled;

public static class ScheduledAgentCredentialRevocationDocumentIds
{
    public static string Build(string agentId, string apiKeyId, string secretReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);

        var segments = new[]
        {
            Encoding.UTF8.GetBytes(agentId),
            Encoding.UTF8.GetBytes(apiKeyId),
            Encoding.UTF8.GetBytes(secretReference),
        };
        var payload = new byte[segments.Sum(static segment => sizeof(uint) + segment.Length)];
        var offset = 0;
        foreach (var segment in segments)
        {
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset, sizeof(uint)), checked((uint)segment.Length));
            offset += sizeof(uint);
            segment.CopyTo(payload, offset);
            offset += segment.Length;
        }

        return "scr1_" + Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
