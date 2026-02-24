using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionStoreDispatcher<TReadModel, TKey>
    : IProjectionStoreDispatcher<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel
{
    private readonly IReadOnlyList<IProjectionStoreBinding<TReadModel, TKey>> _bindings;
    private readonly IReadOnlyList<IProjectionStoreBinding<TReadModel, TKey>> _writeOnlyBindings;
    private readonly IProjectionQueryableStoreBinding<TReadModel, TKey>? _queryBinding;
    private readonly IProjectionStoreDispatchCompensator<TReadModel, TKey> _compensator;
    private readonly ProjectionStoreDispatchOptions _options;
    private readonly ILogger<ProjectionStoreDispatcher<TReadModel, TKey>> _logger;

    public ProjectionStoreDispatcher(
        IEnumerable<IProjectionStoreBinding<TReadModel, TKey>> bindings,
        IProjectionStoreDispatchCompensator<TReadModel, TKey>? compensator = null,
        ProjectionStoreDispatchOptions? options = null,
        ILogger<ProjectionStoreDispatcher<TReadModel, TKey>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        _compensator = compensator ?? NoOpProjectionStoreDispatchCompensator.Instance;
        _options = options ?? new ProjectionStoreDispatchOptions();
        _logger = logger ?? NullLogger<ProjectionStoreDispatcher<TReadModel, TKey>>.Instance;

        _bindings = bindings
            .Where(IsBindingConfigured)
            .ToList();
        if (_bindings.Count == 0)
        {
            throw new InvalidOperationException(
                $"No configured projection store bindings are registered for read model '{typeof(TReadModel).FullName}'.");
        }

        var queryBindings = _bindings
            .OfType<IProjectionQueryableStoreBinding<TReadModel, TKey>>()
            .ToList();
        if (queryBindings.Count > 1)
        {
            throw new InvalidOperationException(
                $"At most one queryable projection store binding is allowed for read model '{typeof(TReadModel).FullName}', but {queryBindings.Count} were registered.");
        }

        _queryBinding = queryBindings.SingleOrDefault();
        _writeOnlyBindings = _queryBinding is null
            ? _bindings
            : _bindings
                .Where(x => !ReferenceEquals(x, _queryBinding))
                .ToList();

        _logger.LogInformation(
            "Projection store dispatcher initialized. readModelType={ReadModelType} bindingCount={BindingCount} queryStore={QueryStore}",
            typeof(TReadModel).FullName,
            _bindings.Count,
            _queryBinding?.StoreName ?? "none");
    }

    public async Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();

        var succeededBindings = new List<IProjectionStoreBinding<TReadModel, TKey>>();
        foreach (var binding in _bindings)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await UpsertWithRetryAsync(binding, readModel, ct);
                succeededBindings.Add(binding);
            }
            catch (Exception ex)
            {
                await CompensateAsync(
                    operation: "upsert",
                    key: default,
                    readModel,
                    succeededBindings,
                    binding,
                    ex,
                    ct);
                throw;
            }
        }
    }

    public async Task MutateAsync(TKey key, Action<TReadModel> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ct.ThrowIfCancellationRequested();

        var queryBinding = GetRequiredQueryBinding();
        await queryBinding.MutateAsync(key, mutate, ct);
        if (_writeOnlyBindings.Count == 0)
            return;

        var updated = await queryBinding.GetAsync(key, ct);
        if (updated == null)
        {
            throw new InvalidOperationException(
                $"Projection store mutate completed but query store '{queryBinding.StoreName}' returned null for read model '{typeof(TReadModel).FullName}'.");
        }

        var succeededWriteOnlyBindings = new List<IProjectionStoreBinding<TReadModel, TKey>>();
        foreach (var binding in _writeOnlyBindings)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await UpsertWithRetryAsync(binding, updated, ct);
                succeededWriteOnlyBindings.Add(binding);
            }
            catch (Exception ex)
            {
                await CompensateAsync(
                    operation: "mutate",
                    key,
                    updated,
                    succeededWriteOnlyBindings,
                    binding,
                    ex,
                    ct);
                throw;
            }
        }
    }

    public Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default)
    {
        return GetRequiredQueryBinding().GetAsync(key, ct);
    }

    public Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        return GetRequiredQueryBinding().ListAsync(take, ct);
    }

    private async Task UpsertWithRetryAsync(
        IProjectionStoreBinding<TReadModel, TKey> binding,
        TReadModel readModel,
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, _options.MaxWriteAttempts);
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await binding.UpsertAsync(readModel, ct);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                lastException = ex;
                _logger.LogWarning(
                    ex,
                    "Projection binding write failed and will retry. readModelType={ReadModelType} store={Store} attempt={Attempt}/{MaxAttempts}",
                    typeof(TReadModel).FullName,
                    binding.StoreName,
                    attempt,
                    maxAttempts);
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        throw new InvalidOperationException(
            $"Projection binding write failed for store '{binding.StoreName}' after {maxAttempts} attempt(s).",
            lastException);
    }

    private Task CompensateAsync(
        string operation,
        TKey? key,
        TReadModel readModel,
        IReadOnlyList<IProjectionStoreBinding<TReadModel, TKey>> succeededBindings,
        IProjectionStoreBinding<TReadModel, TKey> failedBinding,
        Exception exception,
        CancellationToken ct)
    {
        var context = new ProjectionStoreDispatchCompensationContext<TReadModel, TKey>
        {
            Operation = operation,
            Key = key,
            ReadModel = readModel,
            FailedStore = failedBinding.StoreName,
            SucceededStores = succeededBindings.Select(x => x.StoreName).ToList(),
            Exception = exception,
        };
        return _compensator.CompensateAsync(context, ct);
    }

    private IProjectionQueryableStoreBinding<TReadModel, TKey> GetRequiredQueryBinding()
    {
        return _queryBinding ??
               throw new InvalidOperationException(
                   $"Queryable projection store binding is not configured for read model '{typeof(TReadModel).FullName}'.");
    }

    private static bool IsBindingConfigured(IProjectionStoreBinding<TReadModel, TKey> binding)
    {
        return binding is not IProjectionStoreBindingAvailability availability || availability.IsConfigured;
    }

    private sealed class NoOpProjectionStoreDispatchCompensator
        : IProjectionStoreDispatchCompensator<TReadModel, TKey>
    {
        public static NoOpProjectionStoreDispatchCompensator Instance { get; } = new();

        public Task CompensateAsync(
            ProjectionStoreDispatchCompensationContext<TReadModel, TKey> context,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
