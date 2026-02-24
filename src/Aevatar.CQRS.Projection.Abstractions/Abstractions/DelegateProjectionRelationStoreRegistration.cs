namespace Aevatar.CQRS.Projection.Abstractions;

public sealed class DelegateProjectionRelationStoreRegistration
    : IProjectionRelationStoreRegistration
{
    private readonly Func<IServiceProvider, IProjectionRelationStore> _factory;

    public DelegateProjectionRelationStoreRegistration(
        string providerName,
        ProjectionReadModelProviderCapabilities capabilities,
        Func<IServiceProvider, IProjectionRelationStore> factory)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            throw new ArgumentException("Provider name must not be empty.", nameof(providerName));
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(factory);

        ProviderName = providerName.Trim();
        Capabilities = capabilities;
        _factory = factory;
    }

    public string ProviderName { get; }

    public ProjectionReadModelProviderCapabilities Capabilities { get; }

    public IProjectionRelationStore Create(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return _factory(serviceProvider);
    }
}
