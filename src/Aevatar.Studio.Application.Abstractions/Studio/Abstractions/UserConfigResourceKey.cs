namespace Aevatar.Studio.Application.Studio.Abstractions;

public enum UserConfigResourceKind
{
    OwnerScope = 1,
    ChannelBinding = 2,
}

public readonly record struct UserConfigResourceKey(UserConfigResourceKind Kind, string Value)
{
    public static UserConfigResourceKey ForOwnerScope(string scopeId) =>
        new(UserConfigResourceKind.OwnerScope, Normalize(scopeId, nameof(scopeId)));

    public static UserConfigResourceKey ForChannelBinding(string bindingId) =>
        new(UserConfigResourceKind.ChannelBinding, Normalize(bindingId, nameof(bindingId)));

    private static string Normalize(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value.Trim();
    }
}
