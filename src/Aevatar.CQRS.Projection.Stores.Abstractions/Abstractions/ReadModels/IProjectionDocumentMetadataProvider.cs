namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionDocumentMetadataProvider<out TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    DocumentIndexMetadata Metadata { get; }
}
