namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Helpers for one transport-facing bot descriptor.
/// </summary>
public sealed partial class ChannelBotDescriptor
{
    /// <summary>
    /// Creates one normalized bot descriptor.
    /// </summary>
    public static ChannelBotDescriptor Create(
        string registrationId,
        ChannelId channel,
        BotInstanceId bot,
        string? scopeId = null) => new()
    {
        RegistrationId = NormalizeRequired(registrationId, nameof(registrationId)),
        Channel = channel?.Clone() ?? throw new ArgumentNullException(nameof(channel)),
        Bot = bot?.Clone() ?? throw new ArgumentNullException(nameof(bot)),
        ScopeId = NormalizeOptional(scopeId),
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
