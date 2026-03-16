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

internal static class ProjectionTestFactory
{
    public static ContextProjectionActivationService<ServiceProjectionRuntimeLease<TContext>, TContext, IReadOnlyList<string>> CreateActivationService<TContext>(
        Func<string, string, TContext> contextFactory,
        Func<TContext, string> rootActorIdSelector,
        IProjectionLifecycleService<TContext, IReadOnlyList<string>> lifecycle)
        where TContext : class, IProjectionContext
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(rootActorIdSelector);
        ArgumentNullException.ThrowIfNull(lifecycle);

        return new ContextProjectionActivationService<ServiceProjectionRuntimeLease<TContext>, TContext, IReadOnlyList<string>>(
            lifecycle,
            (rootActorId, projectionName, input, commandId, ct) =>
            {
                _ = input;
                _ = commandId;
                _ = ct;
                return contextFactory(rootActorId, projectionName);
            },
            context => new ServiceProjectionRuntimeLease<TContext>(rootActorIdSelector(context), context));
    }
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

    public int LastQueryTake { get; private set; }

    public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        var key = _keySelector(readModel);
        var existingIndex = _items.FindIndex(x => string.Equals(_keySelector(x), key, StringComparison.Ordinal));
        if (existingIndex >= 0)
            _items[existingIndex] = readModel;
        else
            _items.Add(readModel);

        return Task.FromResult(ProjectionWriteResult.Applied());
    }

    public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_items.FirstOrDefault(x => string.Equals(_keySelector(x), key, StringComparison.Ordinal)));

    public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
        ProjectionDocumentQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        LastQueryTake = query.Take;
        IEnumerable<TReadModel> items = _items;

        foreach (var filter in query.Filters)
        {
            items = items.Where(item => MatchesFilter(item, filter));
        }

        var boundedTake = query.Take <= 0 ? 50 : query.Take;
        return Task.FromResult(new ProjectionDocumentQueryResult<TReadModel>
        {
            Items = items.Take(boundedTake).ToList(),
        });
    }

    public Task<IReadOnlyList<TReadModel>> ReadItemsAsync(int take = 50, CancellationToken ct = default)
    {
        LastQueryTake = take;
        return Task.FromResult<IReadOnlyList<TReadModel>>(_items.Take(take).ToList());
    }

    private static bool MatchesFilter(TReadModel item, ProjectionDocumentFilter filter)
    {
        var property = typeof(TReadModel).GetProperty(filter.FieldPath);
        if (property == null)
            return false;

        var value = property.GetValue(item);
        return filter.Operator switch
        {
            ProjectionDocumentFilterOperator.Eq => string.Equals(
                value?.ToString() ?? string.Empty,
                filter.Value.RawValue?.ToString() ?? string.Empty,
                StringComparison.Ordinal),
            _ => throw new NotSupportedException($"Filter operator '{filter.Operator}' is not supported by RecordingDocumentStore."),
        };
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
