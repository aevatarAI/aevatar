using System.Security.Cryptography;
using System.Text;

namespace Aevatar.AI.Abstractions.ToolProviders;

public static class AgentToolArgumentsDigest
{
    public static string Freeze(string? argumentsJson) => argumentsJson ?? string.Empty;

    public static string ComputeSha256(string? argumentsJson)
    {
        var bytes = Encoding.UTF8.GetBytes(Freeze(argumentsJson));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
