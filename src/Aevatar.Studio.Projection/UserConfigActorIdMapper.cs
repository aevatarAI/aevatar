using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Projection;

internal static class UserConfigActorIdMapper
{
    public static string Build(UserConfigResourceKey resource) => resource.Kind switch
    {
        UserConfigResourceKind.OwnerScope => $"user-config-{resource.Value}",
        UserConfigResourceKind.ChannelBinding => $"channel-user-config-{resource.Value}",
        _ => throw new ArgumentOutOfRangeException(nameof(resource)),
    };
}
