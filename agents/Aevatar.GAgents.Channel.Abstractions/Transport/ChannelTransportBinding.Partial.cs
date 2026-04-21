namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Helpers for one adapter bootstrap binding.
/// </summary>
public sealed partial class ChannelTransportBinding
{
    /// <summary>
    /// Creates one normalized transport binding.
    /// </summary>
    public static ChannelTransportBinding Create(
        ChannelBotDescriptor bot,
        string credentialRef,
        string? verificationToken = null) => new()
    {
        Bot = bot?.Clone() ?? throw new ArgumentNullException(nameof(bot)),
        CredentialRef = NormalizeRequired(credentialRef, nameof(credentialRef)),
        VerificationToken = NormalizeOptional(verificationToken),
    };

    private static string NormalizeRequired(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be empty.", paramName);

        return value.Trim();
    }

    private static string NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
