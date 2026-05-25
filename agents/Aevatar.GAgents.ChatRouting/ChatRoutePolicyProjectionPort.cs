using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.ChatRouting;

/// <summary>
/// Activates the per-scope projection runtime for <see cref="ChatRoutePolicyGAgent"/>.
///
/// Unlike <c>DeviceRegistrationGAgent</c> / <c>UserAgentCatalogGAgent</c> (singletons
/// with a well-known id whose projection scope can be primed once at startup), every
/// `chat-route-policy:{scopeId}` actor is its own projection root. The admin REST
/// endpoint calls <see cref="EnsureProjectionForActorAsync"/> right after
/// <c>IActorRuntime.CreateAsync&lt;ChatRoutePolicyGAgent&gt;</c> and before dispatching
/// the <c>UpsertChatRoutePolicyRequested</c> command — without this the actor commits
/// its <c>ChatRoutePolicyUpdated</c> event but no <c>projection.durable.scope:chat-route-policy:&lt;scope&gt;</c>
/// is alive to forward it to <see cref="ChatRoutePolicyCurrentStateProjector"/>,
/// so the readmodel never materializes (the symptom observed on production
/// 2026-05-20: actor created + event committed + GET always 404).
/// </summary>
public sealed class ChatRoutePolicyProjectionPort
    : MaterializationProjectionPortBase<ChatRoutePolicyMaterializationRuntimeLease>
{
    public const string ProjectionKind = "chat-route-policy";

    public ChatRoutePolicyProjectionPort(
        IProjectionScopeActivationService<ChatRoutePolicyMaterializationRuntimeLease> activationService)
        : base(static () => true, activationService)
    {
    }

    public Task<ChatRoutePolicyMaterializationRuntimeLease?> EnsureProjectionForActorAsync(
        string actorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ProjectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);
}
