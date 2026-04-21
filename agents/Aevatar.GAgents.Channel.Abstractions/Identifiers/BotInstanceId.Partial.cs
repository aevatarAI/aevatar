namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Helpers for working with stable bot instance identifiers.
/// </summary>
public sealed partial class BotInstanceId
{
    /// <summary>
    /// Creates one normalized bot instance identifier.
    /// </summary>
    public static BotInstanceId From(string value) => new()
    {
        Value = Normalize(value, nameof(value)),
    };

    /// <summary>
    /// Returns <see langword="true"/> when the identifier is empty.
    /// </summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    private static string Normalize(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Bot instance id cannot be empty.", paramName);

        return value.Trim();
    }
}
