using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Core.LLMProviders;

/// <summary>
/// Composite provider factory: resolves primary providers first and falls back to secondary factory when needed.
/// </summary>
public sealed class FailoverLLMProviderFactory : ILLMProviderFactory
{
    private readonly ILLMProviderFactory _primaryFactory;
    private readonly ILLMProviderFactory _fallbackFactory;
    private readonly LLMProviderFailoverOptions _options;
    private readonly ILogger _logger;

    public FailoverLLMProviderFactory(
        ILLMProviderFactory primaryFactory,
        ILLMProviderFactory fallbackFactory,
        LLMProviderFailoverOptions? options = null,
        ILogger? logger = null)
    {
        _primaryFactory = primaryFactory ?? throw new ArgumentNullException(nameof(primaryFactory));
        _fallbackFactory = fallbackFactory ?? throw new ArgumentNullException(nameof(fallbackFactory));
        _options = options ?? new LLMProviderFailoverOptions();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public ILLMProvider GetProvider(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var primary = TryResolveProvider(() => _primaryFactory.GetProvider(name), "primary", $"provider '{name}'");
        ILLMProvider? fallback;
        if (_options.PreferFallbackDefaultProvider)
        {
            fallback = TryResolveProvider(
                () => _fallbackFactory.GetDefault(),
                "fallback",
                "fallback default provider");
            fallback ??= TryResolveProvider(() => _fallbackFactory.GetProvider(name), "fallback", $"provider '{name}'");
        }
        else
        {
            fallback = TryResolveProvider(() => _fallbackFactory.GetProvider(name), "fallback", $"provider '{name}'");
        }

        if (fallback == null && _options.FallbackToDefaultProviderWhenNamedProviderMissing)
        {
            fallback = TryResolveProvider(
                () => _fallbackFactory.GetDefault(),
                "fallback",
                "fallback default provider");
        }

        if (primary == null && fallback == null)
            throw BuildNoProviderException(name);

        return new FailoverLLMProvider(name, primary, fallback, _logger);
    }

    /// <inheritdoc />
    public ILLMProvider GetDefault()
    {
        var primary = TryResolveProvider(() => _primaryFactory.GetDefault(), "primary", "default provider");
        ILLMProvider? fallback = null;

        if (_options.PreferFallbackDefaultProvider)
        {
            fallback = TryResolveProvider(
                () => _fallbackFactory.GetDefault(),
                "fallback",
                "fallback default provider");
        }

        if (primary != null && fallback == null)
        {
            fallback = TryResolveProvider(
                () => _fallbackFactory.GetProvider(primary.Name),
                "fallback",
                $"provider '{primary.Name}'");

            if (fallback == null && _options.FallbackToDefaultProviderWhenNamedProviderMissing)
            {
                fallback = TryResolveProvider(
                    () => _fallbackFactory.GetDefault(),
                    "fallback",
                    "fallback default provider");
            }
        }
        else
        {
            fallback = TryResolveProvider(
                () => _fallbackFactory.GetDefault(),
                "fallback",
                "default provider");
        }

        if (primary == null && fallback == null)
            throw BuildNoProviderException("(default)");

        return new FailoverLLMProvider(primary?.Name ?? fallback!.Name, primary, fallback, _logger);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableProviders()
    {
        var names = new List<string>();
        AppendDistinct(names, SafeGetAvailableProviders(_primaryFactory));
        AppendDistinct(names, SafeGetAvailableProviders(_fallbackFactory));
        return names;
    }

    private InvalidOperationException BuildNoProviderException(string requested)
    {
        var available = GetAvailableProviders();
        var message = available.Count == 0
            ? $"No LLM providers are registered for requested provider '{requested}'."
            : $"No LLM providers are available for requested provider '{requested}'. Available: {string.Join(", ", available)}";
        return new InvalidOperationException(message);
    }

    private static void AppendDistinct(List<string> target, IEnumerable<string> source)
    {
        foreach (var name in source)
        {
            if (target.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            target.Add(name);
        }
    }

    private IReadOnlyList<string> SafeGetAvailableProviders(ILLMProviderFactory factory)
    {
        try
        {
            return factory.GetAvailableProviders();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failover factory: failed to enumerate available providers.");
            return [];
        }
    }

    private ILLMProvider? TryResolveProvider(
        Func<ILLMProvider> resolver,
        string source,
        string target)
    {
        try
        {
            return resolver();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failover factory: failed to resolve {Target} from {Source}.", target, source);
            return null;
        }
    }
}

/// <summary>
/// Wrapper provider that executes primary first and falls back to secondary provider when primary is invalid.
/// </summary>
public sealed class FailoverLLMProvider : ILLMProvider
{
    private readonly ILLMProvider? _primary;
    private readonly ILLMProvider? _fallback;
    private readonly ILogger _logger;

    public string Name { get; }
    public LLMProviderCapabilities Capabilities => LLMProviderCapabilities.Merge(_primary?.Capabilities, _fallback?.Capabilities);

    public FailoverLLMProvider(
        string name,
        ILLMProvider? primary,
        ILLMProvider? fallback,
        ILogger? logger = null)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? primary?.Name ?? fallback?.Name ?? "llm-failover"
            : name;
        _primary = primary;
        _fallback = fallback;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
    {
        var primary = ResolveCompatiblePrimary(request);
        var fallback = ResolveCompatibleFallback(request);

        if (primary == null)
            return await RequireFallback(fallback).ChatAsync(request, ct);

        if (fallback == null)
            return await primary.ChatAsync(request, ct);

        try
        {
            var response = await primary.ChatAsync(request, ct);
            if (HasUsableOutput(response))
                return response;

            _logger.LogWarning(
                "Failover provider {Name}: primary provider {Primary} returned empty response, switching to fallback {Fallback}.",
                Name,
                primary.Name,
                fallback.Name);
        }
        catch (Exception ex) when (CanFailover(ex, ct))
        {
            _logger.LogWarning(
                ex,
                "Failover provider {Name}: primary provider {Primary} failed, switching to fallback {Fallback}.",
                Name,
                primary.Name,
                fallback.Name);
        }

        return await fallback.ChatAsync(request, ct);
    }

    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var primary = ResolveCompatiblePrimary(request);
        var fallback = ResolveCompatibleFallback(request);

        if (primary == null)
        {
            await foreach (var chunk in RequireFallback(fallback).ChatStreamAsync(request, ct))
                yield return chunk;
            yield break;
        }

        if (fallback == null)
        {
            await foreach (var chunk in primary.ChatStreamAsync(request, ct))
                yield return chunk;
            yield break;
        }

        var emittedMeaningfulChunk = false;
        var shouldFailover = false;
        var preludeChunks = new List<LLMStreamChunk>();

        await using var primaryEnumerator = primary.ChatStreamAsync(request, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            LLMStreamChunk chunk;
            try
            {
                if (!await primaryEnumerator.MoveNextAsync())
                    break;

                chunk = primaryEnumerator.Current;
            }
            catch (Exception ex) when (CanFailover(ex, ct))
            {
                if (emittedMeaningfulChunk)
                    throw;

                _logger.LogWarning(
                    ex,
                    "Failover provider {Name}: primary stream failed before meaningful output, switching to fallback {Fallback}.",
                    Name,
                    fallback.Name);
                shouldFailover = true;
                break;
            }

            if (!emittedMeaningfulChunk)
            {
                if (IsMeaningfulChunk(chunk))
                {
                    emittedMeaningfulChunk = true;
                    foreach (var pending in preludeChunks)
                        yield return pending;
                    preludeChunks.Clear();
                    yield return chunk;
                    continue;
                }

                preludeChunks.Add(chunk);
                continue;
            }

            yield return chunk;
        }

        if (emittedMeaningfulChunk)
            yield break;

        if (!shouldFailover)
        {
            _logger.LogWarning(
                "Failover provider {Name}: primary stream completed without meaningful content/tool chunks, switching to fallback {Fallback}.",
                Name,
                fallback.Name);
        }

        await foreach (var chunk in fallback.ChatStreamAsync(request, ct))
            yield return chunk;
    }

    private ILLMProvider RequireFallback(ILLMProvider? fallback) =>
        fallback ?? throw BuildNoCompatibleProviderException();

    private ILLMProvider? ResolveCompatiblePrimary(LLMRequest request)
    {
        if (_primary == null)
            return null;

        if (_primary.Capabilities.SupportsRequest(request))
            return _primary;

        _logger.LogWarning(
            "Failover provider {Name}: primary provider {Primary} does not support requested modalities, skipping.",
            Name,
            _primary.Name);
        return null;
    }

    private ILLMProvider? ResolveCompatibleFallback(LLMRequest request)
    {
        if (_fallback == null)
            return null;

        if (_fallback.Capabilities.SupportsRequest(request))
            return _fallback;

        _logger.LogWarning(
            "Failover provider {Name}: fallback provider {Fallback} does not support requested modalities, skipping.",
            Name,
            _fallback.Name);
        return null;
    }

    private InvalidOperationException BuildNoCompatibleProviderException()
    {
        var requested = string.Join(", ", Capabilities.SupportedInputModalities);
        return new InvalidOperationException(
            $"No compatible LLM provider is available for failover provider '{Name}'. Advertised modalities: {requested}.");
    }

    private static bool HasUsableOutput(LLMResponse? response)
    {
        if (response == null)
            return false;

        return !string.IsNullOrWhiteSpace(response.Content)
               || response.ContentParts is { Count: > 0 }
               || response.ToolCalls is { Count: > 0 };
    }

    private static bool IsMeaningfulChunk(LLMStreamChunk chunk) =>
        !string.IsNullOrEmpty(chunk.DeltaContent)
        || chunk.DeltaContentPart != null
        || chunk.DeltaToolCall != null;

    private static bool CanFailover(Exception ex, CancellationToken ct) =>
        ex is not OperationCanceledException || !ct.IsCancellationRequested;
}
