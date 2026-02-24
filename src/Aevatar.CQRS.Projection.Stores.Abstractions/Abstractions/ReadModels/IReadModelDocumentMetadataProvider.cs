namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IReadModelDocumentMetadataProvider<out TReadModel>
    where TReadModel : class, IDocumentReadModel
{
    DocumentIndexMetadata Metadata { get; }
}
