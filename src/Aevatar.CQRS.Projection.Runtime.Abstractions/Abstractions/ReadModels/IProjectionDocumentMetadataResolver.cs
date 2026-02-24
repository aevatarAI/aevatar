namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionDocumentMetadataResolver
{
    DocumentIndexMetadata Resolve<TReadModel>()
        where TReadModel : class, IDocumentReadModel;
}
