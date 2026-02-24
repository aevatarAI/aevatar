namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionDocumentMetadataProvider<out TReadModel>
    where TReadModel : class, IDocumentReadModel
{
    DocumentIndexMetadata Metadata { get; }
}
