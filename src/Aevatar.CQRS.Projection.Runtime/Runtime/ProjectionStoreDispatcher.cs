using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionStoreDispatcher<TReadModel, TKey>
    : IProjectionStoreDispatcher<TReadModel, TKey>,
      IProjectionWriteDispatcher<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel
{
    private readonly IReadOnlyList<IProjectionStoreBinding<TReadModel, TKey>> _bindings;
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

        var configuredBindings = new List<IProjectionStoreBinding<TReadModel, TKey>>();
        var skippedBindings = new List<SkippedBindingInfo>();
        foreach (var binding in bindings)
        {
            if (binding is IProjectionStoreBindingAvailability availability &&
                !availability.IsConfigured)
            {
                skippedBindings.Add(new SkippedBindingInfo(binding.StoreName, availability.AvailabilityReason));
                continue;
            }

            configuredBindings.Add(binding);
        }

        foreach (var skipped in skippedBindings)
        {
            _logger.LogInformation(
                "Projection binding skipped. readModelType={ReadModelType} store={Store} reason={Reason}",
                typeof(TReadModel).FullName,
                skipped.StoreName,
                skipped.Reason);
        }

        _bindings = configuredBindings;
        if (_bindings.Count == 0)
        {
            var skipSummary = skippedBindings.Count == 0
                ? "none"
                : string.Join("; ", skippedBindings.Select(x => $"{x.StoreName}: {x.Reason}"));
            throw new InvalidOperationException(
                $"No configured projection store bindings are registered for read model '{typeof(TReadModel).FullName}'. skippedBindings={skipSummary}");
        }

        _logger.LogInformation(
            "Projection store dispatcher initialized. readModelType={ReadModelType} bindingCount={BindingCount}",
            typeof(TReadModel).FullName,
            _bindings.Count);
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
            DispatchId = Guid.NewGuid().ToString("N"),
            Operation = operation,
            Key = key,
            ReadModel = readModel,
            FailedStore = failedBinding.StoreName,
            SucceededStores = succeededBindings.Select(x => x.StoreName).ToList(),
            Exception = exception,
            ReadModelType = typeof(TReadModel).FullName ?? typeof(TReadModel).Name,
            OccurredAtUtc = DateTimeOffset.UtcNow,
        };
        return _compensator.CompensateAsync(context, ct);
    }

    private sealed record SkippedBindingInfo(string StoreName, string Reason);

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
