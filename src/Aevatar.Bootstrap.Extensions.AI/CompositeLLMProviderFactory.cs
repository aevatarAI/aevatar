using System.Collections.Immutable;
using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.Bootstrap.Extensions.AI;

internal sealed class CompositeLLMProviderFactory : ILLMProviderFactory
{
    private readonly ILLMProviderFactory _primaryFactory;
    private readonly ImmutableDictionary<string, ILLMProvider> _additionalProviders;
    private readonly string _defaultName;

    public CompositeLLMProviderFactory(
        ILLMProviderFactory primaryFactory,
        IEnumerable<ILLMProvider> additionalProviders,
        string defaultName)
    {
        _primaryFactory = primaryFactory ?? throw new ArgumentNullException(nameof(primaryFactory));
        _defaultName = string.IsNullOrWhiteSpace(defaultName)
            ? throw new ArgumentException("Default provider name is required.", nameof(defaultName))
            : defaultName.Trim();

        var builder = ImmutableDictionary.CreateBuilder<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in additionalProviders)
        {
            builder[provider.Name] = provider;
        }

        _additionalProviders = builder.ToImmutable();
    }

    public ILLMProvider GetProvider(string name)
    {
        if (_additionalProviders.TryGetValue(name, out var provider))
            return provider;

        return _primaryFactory.GetProvider(name);
    }

    public ILLMProvider GetDefault() => GetProvider(_defaultName);

    public IReadOnlyList<string> GetAvailableProviders() =>
    [
        .. _primaryFactory.GetAvailableProviders(),
        .. _additionalProviders.Keys.Where(name =>
            !_primaryFactory.GetAvailableProviders().Contains(name, StringComparer.OrdinalIgnoreCase)),
    ];
}
