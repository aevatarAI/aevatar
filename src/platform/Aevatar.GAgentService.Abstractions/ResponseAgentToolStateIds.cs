using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgentService.Abstractions;

public static class ResponseAgentToolStateIds
{
    private const string Prefix = "responses-agent-tools-";

    public static string BuildActorId(string scopeId, string ownerSubject)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("scopeId is required.", nameof(scopeId));
        if (string.IsNullOrWhiteSpace(ownerSubject))
            throw new ArgumentException("ownerSubject is required.", nameof(ownerSubject));

        var input = $"{scopeId.Trim()}\n{ownerSubject.Trim()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Prefix + Convert.ToHexString(hash[..16]).ToLowerInvariant();
    }

    public static string NewTaskId() => "task_" + Guid.NewGuid().ToString("N");

    public static string NewWebTraceId() => "web_" + Guid.NewGuid().ToString("N");
}
