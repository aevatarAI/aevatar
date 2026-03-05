using System;

namespace Aevatar.AI.Core.LLMProviders;

/// <summary>
/// Options for cross-provider failover between primary and fallback LLM factories.
/// </summary>
public sealed class LLMProviderFailoverOptions
{
    /// <summary>
    /// When true, failover prefers fallback factory default provider before attempting
    /// same-name provider in fallback factory.
    /// </summary>
    public bool PreferFallbackDefaultProvider { get; init; }

    /// <summary>
    /// When true, a named provider lookup can fallback to fallback-factory default provider
    /// if the same provider name does not exist in fallback.
    /// </summary>
    public bool FallbackToDefaultProviderWhenNamedProviderMissing { get; init; } = true;
}
