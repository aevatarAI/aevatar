namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IDynamicDocumentIndexedReadModel : IProjectionReadModel
{
    string DocumentIndexScope { get; }

    DocumentIndexMetadata DocumentMetadata { get; }
}
