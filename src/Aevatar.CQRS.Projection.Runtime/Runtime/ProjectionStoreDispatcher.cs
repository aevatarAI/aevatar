using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionStoreDispatcher<TReadModel>
    : IProjectionWriteDispatcher<TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    private readonly IProjectionWriteSink<TReadModel> _binding;
    private readonly ILogger<ProjectionStoreDispatcher<TReadModel>> _logger;

    public ProjectionStoreDispatcher(
        IEnumerable<IProjectionWriteSink<TReadModel>> bindings,
        ILogger<ProjectionStoreDispatcher<TReadModel>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        _logger = logger ?? NullLogger<ProjectionStoreDispatcher<TReadModel>>.Instance;

        var enabledBindings = new List<IProjectionWriteSink<TReadModel>>();
        foreach (var binding in bindings)
        {
            if (binding.IsEnabled)
            {
                enabledBindings.Add(binding);
            }
            else
            {
                _logger.LogInformation(
                    "Projection binding skipped. readModelType={ReadModelType} store={Store} reason={Reason}",
                    typeof(TReadModel).FullName,
                    binding.SinkName,
                    binding.DisabledReason);
            }
        }

        if (enabledBindings.Count == 0)
        {
            throw new InvalidOperationException(
                $"No configured projection store bindings are registered for read model '{typeof(TReadModel).FullName}'.");
        }

        if (enabledBindings.Count > 1)
        {
            var names = string.Join(", ", enabledBindings.Select(b => b.SinkName));
            throw new InvalidOperationException(
                $"Multiple projection store bindings registered for read model '{typeof(TReadModel).FullName}': {names}. Only one binding is supported.");
        }

        _binding = enabledBindings[0];

        _logger.LogInformation(
            "Projection store dispatcher initialized. readModelType={ReadModelType} binding={Binding}",
            typeof(TReadModel).FullName,
            _binding.SinkName);
    }

    public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();
        return _binding.UpsertAsync(readModel, ct);
    }

    public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ct.ThrowIfCancellationRequested();
        return _binding.DeleteAsync(id, ct);
    }
}
