namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public sealed class DelegateProjectionStoreRegistration<TStore> : IProjectionStoreRegistration<TStore>
{
    private readonly Func<IServiceProvider, TStore> _factory;

    public DelegateProjectionStoreRegistration(
        string providerName,
        ProjectionProviderCapabilities capabilities,
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

    public ProjectionProviderCapabilities Capabilities { get; }

    public TStore Create(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return _factory(serviceProvider);
    }
}
