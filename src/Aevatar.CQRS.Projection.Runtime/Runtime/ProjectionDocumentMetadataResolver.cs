using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionDocumentMetadataResolver : IProjectionDocumentMetadataResolver
{
    private readonly IServiceProvider _serviceProvider;

    public ProjectionDocumentMetadataResolver(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public DocumentIndexMetadata Resolve<TReadModel>()
        where TReadModel : class, IDocumentReadModel
    {
        var provider = _serviceProvider.GetRequiredService<IReadModelDocumentMetadataProvider<TReadModel>>();
        return provider.Metadata;
    }
}
