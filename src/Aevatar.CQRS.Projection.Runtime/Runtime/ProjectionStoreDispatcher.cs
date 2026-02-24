using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionStoreDispatcher<TReadModel, TKey>
    : IProjectionStoreDispatcher<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel
{
    private readonly IReadOnlyList<IProjectionStoreBinding<TReadModel, TKey>> _bindings;
    private readonly IReadOnlyList<IProjectionStoreBinding<TReadModel, TKey>> _writeOnlyBindings;
    private readonly IProjectionQueryableStoreBinding<TReadModel, TKey> _queryBinding;
    private readonly ILogger<ProjectionStoreDispatcher<TReadModel, TKey>> _logger;

    public ProjectionStoreDispatcher(
        IEnumerable<IProjectionStoreBinding<TReadModel, TKey>> bindings,
        ILogger<ProjectionStoreDispatcher<TReadModel, TKey>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        _logger = logger ?? NullLogger<ProjectionStoreDispatcher<TReadModel, TKey>>.Instance;

        _bindings = bindings.ToList();
        if (_bindings.Count == 0)
        {
            throw new InvalidOperationException(
                $"No projection store bindings are registered for read model '{typeof(TReadModel).FullName}'.");
        }

        var queryBindings = _bindings
            .OfType<IProjectionQueryableStoreBinding<TReadModel, TKey>>()
            .ToList();
        if (queryBindings.Count != 1)
        {
            throw new InvalidOperationException(
                $"Exactly one queryable projection store binding is required for read model '{typeof(TReadModel).FullName}', but {queryBindings.Count} were registered.");
        }

        _queryBinding = queryBindings[0];
        _writeOnlyBindings = _bindings
            .Where(x => x is not IProjectionQueryableStoreBinding<TReadModel, TKey>)
            .ToList();

        _logger.LogInformation(
            "Projection store dispatcher initialized. readModelType={ReadModelType} bindingCount={BindingCount} queryStore={QueryStore}",
            typeof(TReadModel).FullName,
            _bindings.Count,
            _queryBinding.StoreName);
    }

    public async Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();

        foreach (var binding in _bindings)
        {
            ct.ThrowIfCancellationRequested();
            await binding.UpsertAsync(readModel, ct);
        }
    }

    public async Task MutateAsync(TKey key, Action<TReadModel> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ct.ThrowIfCancellationRequested();

        await _queryBinding.MutateAsync(key, mutate, ct);
        if (_writeOnlyBindings.Count == 0)
            return;

        var updated = await _queryBinding.GetAsync(key, ct);
        if (updated == null)
        {
            throw new InvalidOperationException(
                $"Projection store mutate completed but query store '{_queryBinding.StoreName}' returned null for read model '{typeof(TReadModel).FullName}'.");
        }

        foreach (var binding in _writeOnlyBindings)
        {
            ct.ThrowIfCancellationRequested();
            await binding.UpsertAsync(updated, ct);
        }
    }

    public Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default)
    {
        return _queryBinding.GetAsync(key, ct);
    }

    public Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        return _queryBinding.ListAsync(take, ct);
    }
}
