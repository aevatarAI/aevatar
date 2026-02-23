namespace Aevatar.CQRS.Projection.Abstractions;

public sealed class DelegateProjectionReadModelStoreRegistration<TReadModel, TKey>
    : IProjectionReadModelStoreRegistration<TReadModel, TKey>
    where TReadModel : class
{
    private readonly Func<IServiceProvider, IProjectionReadModelStore<TReadModel, TKey>> _factory;

    public DelegateProjectionReadModelStoreRegistration(
        string providerName,
        ProjectionReadModelProviderCapabilities capabilities,
        Func<IServiceProvider, IProjectionReadModelStore<TReadModel, TKey>> factory)
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

    public IProjectionReadModelStore<TReadModel, TKey> Create(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return _factory(serviceProvider);
    }
}
