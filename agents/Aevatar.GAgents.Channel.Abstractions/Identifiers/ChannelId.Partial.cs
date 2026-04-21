namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Helpers for working with stable channel identifiers.
/// </summary>
public sealed partial class ChannelId
{
    /// <summary>
    /// Creates one normalized channel identifier.
    /// </summary>
    public static ChannelId From(string value) => new()
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
            throw new ArgumentException("Channel id cannot be empty.", paramName);

        return value.Trim();
    }
}
