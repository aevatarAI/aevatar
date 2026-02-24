namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IDocumentReadModel : IProjectionReadModel
{
    string DocumentScope { get; }
}
