using Microsoft.AspNetCore.Routing;

namespace Aevatar.Bootstrap.Hosting;

public sealed class AevatarCapabilityRegistration
{
    public required string Name { get; init; }

    public required Action<IEndpointRouteBuilder> MapEndpoints { get; init; }
}
