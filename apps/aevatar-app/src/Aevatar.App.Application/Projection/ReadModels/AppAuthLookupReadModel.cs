using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.App.Application.Projection.ReadModels;

public sealed class AppAuthLookupReadModel : IProjectionReadModel
{
    public string Id { get; set; } = "";
    public string LookupKey { get; set; } = "";
    public string UserId { get; set; } = "";
}
