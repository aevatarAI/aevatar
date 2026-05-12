using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgentService.Abstractions;

public static class ResponseAgentToolStateIds
{
    private const string Prefix = "responses-agent-tools-";

    /// <summary>
    /// Build a deterministic actor id from <paramref name="scopeId"/> and
    /// <paramref name="ownerSubject"/>. The id is the prefix + the first
    /// 16 bytes (128 bits) of a SHA-256 over the joined inputs.
    ///
    /// Trade-off: a hashed key is opaque — operators reading the actor id
    /// can't see which scope/owner it belongs to. The reason it's hashed
    /// rather than concatenated is that scope and owner values come from
    /// upstream identity providers and can contain characters that aren't
    /// safe in an actor id key (slashes, colons, whitespace, UTF-8 emoji).
    /// At 128 bits the birthday-collision space is ~2^64 distinct pairs
    /// before a coincidence is expected, which is well past the lifetime
    /// scale of any realistic deployment; the authoritative scope/owner
    /// fields are still carried in
    /// <see cref="ResponsesAgentToolStateRecord"/> for audit / debugging.
    /// </summary>
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
