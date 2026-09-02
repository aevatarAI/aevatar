using Google.Protobuf;

namespace Aevatar.AI.ToolProviders.Ornn;

internal static class OrnnSkillSha256Parser
{
    private const int Sha256Length = 32;

    internal static bool TryParse(string? value, out ByteString sha256)
    {
        sha256 = ByteString.Empty;
        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace))
            return false;

        byte[] bytes;
        if (value.Length == Sha256Length * 2 && value.All(Uri.IsHexDigit))
        {
            try
            {
                bytes = Convert.FromHexString(value);
            }
            catch (FormatException)
            {
                return false;
            }
        }
        else
        {
            try
            {
                bytes = Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                return false;
            }

            if (!string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal))
                return false;
        }

        if (bytes.Length != Sha256Length)
            return false;
        sha256 = ByteString.CopyFrom(bytes);
        return true;
    }
}
