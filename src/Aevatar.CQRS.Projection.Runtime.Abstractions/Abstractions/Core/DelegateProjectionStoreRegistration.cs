namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public sealed class DelegateProjectionStoreRegistration<TStore> : IProjectionStoreRegistration<TStore>
{
    private readonly Func<IServiceProvider, TStore> _factory;

    public DelegateProjectionStoreRegistration(
        string providerName,
        Func<IServiceProvider, TStore> factory)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            throw new ArgumentException("Provider name must not be empty.", nameof(providerName));
        ArgumentNullException.ThrowIfNull(factory);

        ProviderName = providerName.Trim();
        _factory = factory;
    }

    public string ProviderName { get; }

    public TStore Create(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return _factory(serviceProvider);
    }
}
