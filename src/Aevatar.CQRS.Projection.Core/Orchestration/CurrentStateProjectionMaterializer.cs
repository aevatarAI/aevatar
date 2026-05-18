using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal sealed class CurrentStateProjectionMaterializer<TContext, TState, TReadModel>
    : ICurrentStateProjectionMaterializer<TContext>
    where TContext : class, IProjectionMaterializationContext
    where TState : class, IMessage<TState>, new()
    where TReadModel : class, IProjectionReadModel
{
    private readonly IProjectionWriteDispatcher<TReadModel> _writeDispatcher;
    private readonly IProjectionClock _clock;
    private readonly Func<TContext, TState, CurrentStateProjectionInfo, TReadModel> _map;
    private readonly ILogger<CurrentStateProjectionMaterializer<TContext, TState, TReadModel>> _logger;

    public CurrentStateProjectionMaterializer(
        IProjectionWriteDispatcher<TReadModel> writeDispatcher,
        IProjectionClock clock,
        Func<TContext, TState, CurrentStateProjectionInfo, TReadModel> map,
        ILogger<CurrentStateProjectionMaterializer<TContext, TState, TReadModel>>? logger = null)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _logger = logger ?? NullLogger<CurrentStateProjectionMaterializer<TContext, TState, TReadModel>>.Instance;
    }

    public async ValueTask ProjectAsync(
        TContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();

        if (!CommittedStateEventEnvelope.TryUnpackState<TState>(
                envelope,
                out var published,
                out var stateEvent,
                out var state) ||
            published == null ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var info = new CurrentStateProjectionInfo(
            RootActorId: context.RootActorId,
            CommandId: envelope.Propagation?.CausationEventId ?? string.Empty,
            CorrelationId: envelope.Propagation?.CorrelationId ?? string.Empty,
            StateVersion: stateEvent.Version,
            LastEventId: stateEvent.EventId ?? string.Empty,
            ObservedAt: CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
            Envelope: envelope,
            ObservedPayload: stateEvent.EventData);
        var readModel = _map(context, state, info);
        CurrentStateProjectionReadModelFields.Apply(readModel, info);
        var result = await _writeDispatcher.UpsertAsync(readModel, ct);
        if (result.IsRejected)
        {
            _logger.LogWarning(
                "Current-state projection write rejected. contextType={ContextType} stateType={StateType} readModelType={ReadModelType} rootActorId={RootActorId} stateVersion={StateVersion} lastEventId={LastEventId} disposition={Disposition}",
                typeof(TContext).FullName,
                typeof(TState).FullName,
                typeof(TReadModel).FullName,
                info.RootActorId,
                info.StateVersion,
                info.LastEventId,
                result.Disposition);
        }
    }
}

internal static class CurrentStateProjectionReadModelFields
{
    private static readonly string[] ActorIdPropertyNames = ["RootActorId", "ActorId"];

    public static void Apply<TReadModel>(TReadModel readModel, CurrentStateProjectionInfo info)
        where TReadModel : class, IProjectionReadModel
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ArgumentNullException.ThrowIfNull(info);

        SetRequiredString(readModel, nameof(IProjectionReadModel.Id), info.RootActorId);
        SetFirstAvailableRequiredString(readModel, ActorIdPropertyNames, info.RootActorId);
        SetRequiredInt64(readModel, nameof(IProjectionReadModel.StateVersion), info.StateVersion);
        SetRequiredString(readModel, nameof(IProjectionReadModel.LastEventId), info.LastEventId);
        SetRequiredDateTimeOffset(readModel, nameof(IProjectionReadModel.UpdatedAt), info.ObservedAt);
    }

    private static void SetFirstAvailableRequiredString<TReadModel>(
        TReadModel readModel,
        IReadOnlyList<string> propertyNames,
        string value)
        where TReadModel : class, IProjectionReadModel
    {
        foreach (var propertyName in propertyNames)
        {
            if (TrySetProperty(readModel, propertyName, value))
                return;
        }

        throw MissingWritableProperty<TReadModel>(string.Join(" or ", propertyNames));
    }

    private static void SetRequiredString<TReadModel>(TReadModel readModel, string propertyName, string value)
        where TReadModel : class, IProjectionReadModel
    {
        if (!TrySetProperty(readModel, propertyName, value))
            throw MissingWritableProperty<TReadModel>(propertyName);
    }

    private static void SetRequiredInt64<TReadModel>(TReadModel readModel, string propertyName, long value)
        where TReadModel : class, IProjectionReadModel
    {
        if (!TrySetProperty(readModel, propertyName, value))
            throw MissingWritableProperty<TReadModel>(propertyName);
    }

    private static void SetRequiredDateTimeOffset<TReadModel>(
        TReadModel readModel,
        string propertyName,
        DateTimeOffset value)
        where TReadModel : class, IProjectionReadModel
    {
        if (!TrySetProperty(readModel, propertyName, value))
            throw MissingWritableProperty<TReadModel>(propertyName);
    }

    private static bool TrySetProperty<TReadModel>(
        TReadModel readModel,
        string propertyName,
        object value)
        where TReadModel : class, IProjectionReadModel
    {
        var property = typeof(TReadModel).GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public);
        if (property?.SetMethod == null)
            return false;

        property.SetValue(readModel, value);
        return true;
    }

    private static InvalidOperationException MissingWritableProperty<TReadModel>(string propertyName) =>
        new(
            $"Current-state read model '{typeof(TReadModel).FullName}' must expose writable '{propertyName}' for framework-owned projection fields.");
}
