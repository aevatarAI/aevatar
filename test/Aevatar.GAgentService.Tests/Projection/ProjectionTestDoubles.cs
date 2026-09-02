using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Governance.Projection.Orchestration;
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

    public bool EnforceMonotonicWrites { get; set; }

    public int LastQueryTake { get; private set; }

    public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        var key = _keySelector(readModel);
        var existingIndex = _items.FindIndex(x => string.Equals(_keySelector(x), key, StringComparison.Ordinal));
        var existing = existingIndex >= 0 ? _items[existingIndex] : null;
        var result = EnforceMonotonicWrites
            ? ProjectionWriteResultEvaluator.Evaluate(existing, readModel)
            : ProjectionWriteResult.Applied();
        if (result.IsApplied)
        {
            if (existingIndex >= 0)
                _items[existingIndex] = readModel;
            else
                _items.Add(readModel);
        }

        return Task.FromResult(result);
    }

    public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
    {
        var existingIndex = _items.FindIndex(x => string.Equals(_keySelector(x), id, StringComparison.Ordinal));
        if (existingIndex < 0)
            return Task.FromResult(ProjectionWriteResult.Duplicate());

        _items.RemoveAt(existingIndex);
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
            ProjectionDocumentFilterOperator.Gte => Compare(value, filter.Value.RawValue) >= 0,
            ProjectionDocumentFilterOperator.Lte => Compare(value, filter.Value.RawValue) <= 0,
            _ => throw new NotSupportedException($"Filter operator '{filter.Operator}' is not supported by RecordingDocumentStore."),
        };
    }

    private static int Compare(object? left, object? right)
    {
        var normalizedLeft = NormalizeComparable(left);
        var normalizedRight = NormalizeComparable(right);
        return normalizedLeft.CompareTo(normalizedRight);
    }

    private static IComparable NormalizeComparable(object? value) =>
        value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime,
            DateTime dateTime => dateTime.Kind == DateTimeKind.Utc ? dateTime : dateTime.ToUniversalTime(),
            IComparable comparable => comparable,
            _ => string.Empty,
        };
}

internal sealed class RecordingProjectionActivationService<TContext>
    : IProjectionScopeActivationService<ServiceProjectionRuntimeLease<TContext>>
    where TContext : class, IProjectionMaterializationContext
{
    private readonly Func<string, string, TContext> _contextFactory;

    public RecordingProjectionActivationService(Func<string, string, TContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public List<(string rootEntityId, string projectionName)> Calls { get; } = [];
    public List<ProjectionScopeStartRequest> Requests { get; } = [];

    public Task<ServiceProjectionRuntimeLease<TContext>> EnsureAsync(
        ProjectionScopeStartRequest request,
        CancellationToken ct = default)
    {
        Calls.Add((request.RootActorId, request.ProjectionKind));
        Requests.Add(request);
        return Task.FromResult(new ServiceProjectionRuntimeLease<TContext>(
            request.RootActorId,
            _contextFactory(request.RootActorId, request.ProjectionKind)));
    }
}

internal sealed class RecordingProjectionReleaseService<TLease>
    : IProjectionScopeReleaseService<TLease>
    where TLease : class, IProjectionRuntimeLease
{
    public List<TLease> Released { get; } = [];

    public Exception? Exception { get; init; }

    public Task ReleaseIfIdleAsync(TLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        Released.Add(lease);
        return Exception == null
            ? Task.CompletedTask
            : Task.FromException(Exception);
    }
}

internal sealed class NoOpProjectionReleaseService<TLease>
    : IProjectionScopeReleaseService<TLease>
    where TLease : class, IProjectionRuntimeLease
{
    public Task ReleaseIfIdleAsync(TLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return Task.CompletedTask;
    }
}

internal sealed class NoOpServiceConfigurationReleaseService
    : IProjectionScopeReleaseService<ServiceConfigurationRuntimeLease>
{
    public Task ReleaseIfIdleAsync(ServiceConfigurationRuntimeLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingActorRuntime : IActorRuntime
{
    public HashSet<string> KnownActorIds { get; } = [];

    public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
        where TAgent : IAgent =>
        throw new NotSupportedException();

    public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

    public Task<bool> ExistsAsync(string id) =>
        Task.FromResult(KnownActorIds.Contains(id));

    public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

    public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
}
