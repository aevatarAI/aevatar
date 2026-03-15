using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Projection.Orchestration;

namespace Aevatar.GAgentService.Tests.Projection;

internal sealed class FixedProjectionClock : IProjectionClock
{
    public FixedProjectionClock(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; }
}

internal sealed class RecordingDocumentStore<TReadModel> :
    IProjectionDocumentReader<TReadModel, string>,
    IProjectionWriteDispatcher<TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    private readonly Func<TReadModel, string> _keySelector;
    private readonly List<TReadModel> _items = [];

    public RecordingDocumentStore(Func<TReadModel, string> keySelector)
    {
        _keySelector = keySelector;
    }

    public int LastListTake { get; private set; }

    public Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        var key = _keySelector(readModel);
        var existingIndex = _items.FindIndex(x => string.Equals(_keySelector(x), key, StringComparison.Ordinal));
        if (existingIndex >= 0)
            _items[existingIndex] = readModel;
        else
            _items.Add(readModel);

        return Task.CompletedTask;
    }

    public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_items.FirstOrDefault(x => string.Equals(_keySelector(x), key, StringComparison.Ordinal)));

    public Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        LastListTake = take;
        return Task.FromResult<IReadOnlyList<TReadModel>>(_items.Take(take).ToList());
    }
}

internal sealed class RecordingProjectionActivationService<TContext>
    : IProjectionPortActivationService<ServiceProjectionRuntimeLease<TContext>>
    where TContext : class, IProjectionContext
{
    private readonly Func<string, string, TContext> _contextFactory;

    public RecordingProjectionActivationService(Func<string, string, TContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public List<(string rootEntityId, string projectionName, string input, string commandId)> Calls { get; } = [];

    public Task<ServiceProjectionRuntimeLease<TContext>> EnsureAsync(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct = default)
    {
        Calls.Add((rootEntityId, projectionName, input, commandId));
        return Task.FromResult(new ServiceProjectionRuntimeLease<TContext>(
            rootEntityId,
            _contextFactory(rootEntityId, projectionName)));
    }
}

internal sealed class RecordingProjectionLifecycle<TContext>
    : IProjectionLifecycleService<TContext, IReadOnlyList<string>>
    where TContext : class, IProjectionContext
{
    public List<TContext> StartedContexts { get; } = [];

    public List<TContext> StoppedContexts { get; } = [];

    public Task StartAsync(TContext context, CancellationToken ct = default)
    {
        StartedContexts.Add(context);
        return Task.CompletedTask;
    }

    public Task ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task StopAsync(TContext context, CancellationToken ct = default)
    {
        StoppedContexts.Add(context);
        return Task.CompletedTask;
    }

    public Task CompleteAsync(TContext context, IReadOnlyList<string> completion, CancellationToken ct = default) =>
        Task.CompletedTask;
}
