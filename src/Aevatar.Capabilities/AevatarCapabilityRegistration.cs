using Microsoft.AspNetCore.Routing;

namespace Aevatar.Capabilities;

public sealed class AevatarCapabilityRegistration
{
    public required string Name { get; init; }

    public required Action<IEndpointRouteBuilder> MapEndpoints { get; init; }
}
