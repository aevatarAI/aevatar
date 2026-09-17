namespace Aevatar.Foundation.Abstractions;

/// <summary>
/// An open canonical Channel platform identity. This value validates identity syntax;
/// it does not determine which platforms have message adapters or may be adopted.
/// </summary>
public sealed record ChannelPlatformId
{
    private ChannelPlatformId(string value) => Value = value;

    public string Value { get; }

    /// <summary>Normalize once at a Host or third-party adapter boundary.</summary>
    public static ChannelPlatformId ParseExternal(string? raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        if (raw.Any(char.IsControl))
            throw new ArgumentException("Channel platform cannot contain control characters.", nameof(raw));
        var value = raw.Trim().ToLowerInvariant();
        ValidateCharacters(value);
        return new ChannelPlatformId(value);
    }

    /// <summary>Require an already canonical identity inside the application.</summary>
    public static ChannelPlatformId FromCanonical(string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ValidateCharacters(value);
        if (!string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
            throw new ArgumentException("Channel platform must already be canonical.", nameof(value));
        return new ChannelPlatformId(value);
    }

    private static void ValidateCharacters(string value)
    {
        if (value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new ArgumentException("Channel platform cannot contain whitespace or control characters.", nameof(value));
    }

    public override string ToString() => Value;
}
