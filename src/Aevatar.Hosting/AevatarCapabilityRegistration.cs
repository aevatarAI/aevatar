using Microsoft.AspNetCore.Routing;

namespace Aevatar.Hosting;

public sealed class AevatarCapabilityRegistration
{
    public required string Name { get; init; }

    public required Action<IEndpointRouteBuilder> MapEndpoints { get; init; }
}
