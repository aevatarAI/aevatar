using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionStoreDispatcher<TReadModel>
    : IProjectionWriteDispatcher<TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    private readonly IReadOnlyList<IProjectionWriteSink<TReadModel>> _bindings;
    private readonly IProjectionStoreDispatchCompensator<TReadModel> _compensator;
    private readonly ProjectionStoreDispatchOptions _options;
    private readonly ILogger<ProjectionStoreDispatcher<TReadModel>> _logger;

    public ProjectionStoreDispatcher(
        IEnumerable<IProjectionWriteSink<TReadModel>> bindings,
        IProjectionStoreDispatchCompensator<TReadModel>? compensator = null,
        ProjectionStoreDispatchOptions? options = null,
        ILogger<ProjectionStoreDispatcher<TReadModel>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        _compensator = compensator ?? NoOpProjectionStoreDispatchCompensator.Instance;
        _options = options ?? new ProjectionStoreDispatchOptions();
        _logger = logger ?? NullLogger<ProjectionStoreDispatcher<TReadModel>>.Instance;

        var configuredBindings = new List<IProjectionWriteSink<TReadModel>>();
        var skippedBindings = new List<SkippedBindingInfo>();
        foreach (var binding in bindings)
        {
            if (!binding.IsEnabled)
            {
                skippedBindings.Add(new SkippedBindingInfo(binding.SinkName, binding.DisabledReason));
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

        var succeededBindings = new List<IProjectionWriteSink<TReadModel>>();
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
        IProjectionWriteSink<TReadModel> binding,
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
                    binding.SinkName,
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
            $"Projection binding write failed for store '{binding.SinkName}' after {maxAttempts} attempt(s).",
            lastException);
    }

    private Task CompensateAsync(
        string operation,
        TReadModel readModel,
        IReadOnlyList<IProjectionWriteSink<TReadModel>> succeededBindings,
        IProjectionWriteSink<TReadModel> failedBinding,
        Exception exception,
        CancellationToken ct)
    {
        var context = new ProjectionStoreDispatchCompensationContext<TReadModel>
        {
            DispatchId = Guid.NewGuid().ToString("N"),
            Operation = operation,
            ReadModel = readModel,
            FailedStore = failedBinding.SinkName,
            SucceededStores = succeededBindings.Select(x => x.SinkName).ToList(),
            Exception = exception,
            ReadModelType = typeof(TReadModel).FullName ?? typeof(TReadModel).Name,
            OccurredAtUtc = DateTimeOffset.UtcNow,
        };
        return _compensator.CompensateAsync(context, ct);
    }

    private sealed record SkippedBindingInfo(string StoreName, string Reason);

    private sealed class NoOpProjectionStoreDispatchCompensator
        : IProjectionStoreDispatchCompensator<TReadModel>
    {
        public static NoOpProjectionStoreDispatchCompensator Instance { get; } = new();

        public Task CompensateAsync(
            ProjectionStoreDispatchCompensationContext<TReadModel> context,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
