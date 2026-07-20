using System.Text;

namespace Aevatar.Audit.Core.Identity;

internal static class AuditActorIdentityHasherKeyMaterial
{
    public const int MinimumKeyBytes = 32;

    public static byte[] Resolve(AuditActorIdentityHasherKeyOptions key)
    {
        if (!string.IsNullOrWhiteSpace(key.KeyBase64))
        {
            return Convert.FromBase64String(key.KeyBase64.Trim());
        }

        return Encoding.UTF8.GetBytes(key.Key!.Trim());
    }
}
