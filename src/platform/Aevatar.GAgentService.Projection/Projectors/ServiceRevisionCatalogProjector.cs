using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class ServiceRevisionCatalogProjector
    : IProjectionArtifactMaterializer<ServiceRevisionCatalogProjectionContext>
{
    private readonly IProjectionWriteDispatcher<ServiceRevisionCatalogReadModel> _storeDispatcher;
    private readonly IProjectionClock _clock;

    public ServiceRevisionCatalogProjector(
        IProjectionWriteDispatcher<ServiceRevisionCatalogReadModel> storeDispatcher,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(ServiceRevisionCatalogProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<ServiceRevisionCatalogState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null ||
            state.Identity == null)
        {
            return;
        }

        var serviceKey = ServiceKeys.Build(state.Identity);
        if (string.IsNullOrWhiteSpace(serviceKey))
        {
            return;
        }

        var readModel = new ServiceRevisionCatalogReadModel
        {
            Id = serviceKey,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
            Revisions = state.Revisions
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => MapRevision(x.Key, x.Value))
                .ToList(),
        };
        await _storeDispatcher.UpsertAsync(readModel, ct);
    }

    private static ServiceRevisionEntryReadModel MapRevision(
        string revisionId,
        ServiceRevisionRecordState state)
    {
        return new ServiceRevisionEntryReadModel
        {
            RevisionId = revisionId?.Trim() ?? string.Empty,
            ImplementationKind = ResolveImplementationKind(state),
            Status = state.Status.ToString(),
            ArtifactHash = state.ArtifactHash ?? string.Empty,
            FailureReason = state.FailureReason ?? string.Empty,
            CreatedAt = ToDateTimeOffset(state.CreatedAt),
            PreparedAt = ToDateTimeOffset(state.PreparedAt),
            PublishedAt = ToDateTimeOffset(state.PublishedAt),
            RetiredAt = ToDateTimeOffset(state.RetiredAt),
            Endpoints = state.Endpoints
                .Select(MapEndpoint)
                .OrderBy(x => x.EndpointId, StringComparer.Ordinal)
                .ToList(),
            StaticActorTypeName = state.Spec?.StaticSpec?.ActorTypeName ?? string.Empty,
            StaticPreferredActorId = state.Spec?.StaticSpec?.PreferredActorId ?? string.Empty,
            ScriptingScriptId = state.Spec?.ScriptingSpec?.ScriptId ?? string.Empty,
            ScriptingRevision = state.Spec?.ScriptingSpec?.Revision ?? string.Empty,
            ScriptingDefinitionActorId = state.Spec?.ScriptingSpec?.DefinitionActorId ?? string.Empty,
            ScriptingSourceHash = state.Spec?.ScriptingSpec?.SourceHash ?? string.Empty,
            WorkflowName = state.Spec?.WorkflowSpec?.WorkflowName ?? string.Empty,
            WorkflowDefinitionActorId = state.Spec?.WorkflowSpec?.DefinitionActorId ?? string.Empty,
            WorkflowInlineWorkflowCount = state.Spec?.WorkflowSpec?.InlineWorkflowYamls?.Count ?? 0,
        };
    }

    private static string ResolveImplementationKind(ServiceRevisionRecordState state)
    {
        return state.Spec?.ImplementationKind.ToString() ??
               ServiceImplementationKind.Unspecified.ToString();
    }

    private static ServiceCatalogEndpointReadModel MapEndpoint(ServiceEndpointDescriptor endpoint) =>
        new()
        {
            EndpointId = endpoint.EndpointId ?? string.Empty,
            DisplayName = endpoint.DisplayName ?? string.Empty,
            Kind = endpoint.Kind.ToString(),
            RequestTypeUrl = endpoint.RequestTypeUrl ?? string.Empty,
            ResponseTypeUrl = endpoint.ResponseTypeUrl ?? string.Empty,
            Description = endpoint.Description ?? string.Empty,
        };

    private static DateTimeOffset? ToDateTimeOffset(Timestamp? timestamp)
    {
        return timestamp == null ? null : timestamp.ToDateTimeOffset();
    }
}
