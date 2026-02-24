namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionDocumentMetadataResolver
{
    DocumentIndexMetadata Resolve<TReadModel>()
        where TReadModel : class, IDocumentReadModel;
}
