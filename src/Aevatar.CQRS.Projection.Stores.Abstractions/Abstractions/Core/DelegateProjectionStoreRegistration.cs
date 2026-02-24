namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed class DelegateProjectionStoreRegistration<TStore> : IProjectionStoreRegistration<TStore>
{
    private readonly Func<IServiceProvider, TStore> _factory;

    public DelegateProjectionStoreRegistration(
        string providerName,
        ProjectionReadModelProviderCapabilities capabilities,
        Func<IServiceProvider, TStore> factory)
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

    public TStore Create(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return _factory(serviceProvider);
    }
}
