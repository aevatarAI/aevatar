using System.Collections.Immutable;
using Aevatar.AI.Abstractions.LLMProviders;
using Microsoft.Extensions.Logging;

namespace Aevatar.AI.LLMProviders.NyxId;

public sealed class NyxIdLLMProviderFactory : ILLMProviderFactory
{
    private ImmutableDictionary<string, NyxIdLLMProvider> _providers =
        ImmutableDictionary<string, NyxIdLLMProvider>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    private string _defaultName = string.Empty;

    public NyxIdLLMProviderFactory RegisterGateway(
        string name,
        string defaultModel,
        string gatewayEndpoint,
        Func<string?> accessTokenAccessor,
        ILogger? logger = null)
    {
        var provider = new NyxIdLLMProvider(name, defaultModel, gatewayEndpoint, accessTokenAccessor, logger);
        ImmutableInterlocked.AddOrUpdate(ref _providers, provider.Name, provider, (_, _) => provider);
        if (string.IsNullOrWhiteSpace(Volatile.Read(ref _defaultName)))
            Volatile.Write(ref _defaultName, provider.Name);

        return this;
    }

    public NyxIdLLMProviderFactory SetDefault(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Provider name is required.", nameof(name));

        Volatile.Write(ref _defaultName, name.Trim());
        return this;
    }

    public ILLMProvider GetProvider(string name) =>
        _providers.TryGetValue(name, out var provider)
            ? provider
            : throw new InvalidOperationException($"NyxID provider '{name}' is not registered.");

    public ILLMProvider GetDefault()
    {
        var defaultName = Volatile.Read(ref _defaultName);
        return GetProvider(defaultName);
    }

    public IReadOnlyList<string> GetAvailableProviders() => _providers.Keys.ToList();
}
