using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Hosting;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public sealed record NyxIdChatConversationCreateReceipt(
    NyxIdChatConversationCreateStatus Status,
    string? ActorId,
    Reject? Reject);

public enum NyxIdChatConversationCreateStatus
{
    Accepted = 0,
    RouteRejected = 1,
    RegistrationUnavailable = 2,
}

public sealed record NyxIdChatConversationDeleteReceipt(
    NyxIdChatConversationDeleteStatus Status);

public enum NyxIdChatConversationDeleteStatus
{
    Accepted = 0,
    NotFound = 1,
    AccessDenied = 2,
    AdmissionUnavailable = 3,
}

// Refactor (iter47/issue-877-chat-endpoints-own-lifecycle-and-compensation):
//   Old pattern: Chat endpoints owned actor lifecycle, registry compensation, participant orchestration, terminal-state recovery, and IChatHistoryStore side effects.
//   New principle: Endpoint is adapter-only (HTTP/SSE); typed command facade owns lifecycle; existing chat actors own compensation events and terminal-state publication.
public sealed class NyxIdChatLifecycleFacade
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IGAgentActorRegistryCommandPort _registryCommandPort;
    private readonly IChatRoutePolicyQueryPort _routeQueryPort;
    private readonly ChatRouteResolver _routeResolver;
    private readonly IScopeResourceAdmissionPort _admissionPort;
    private readonly IChatHistoryStore _chatHistoryStore;
    private readonly ILogger<NyxIdChatLifecycleFacade> _logger;

    public NyxIdChatLifecycleFacade(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort,
        IGAgentActorRegistryCommandPort registryCommandPort,
        IChatRoutePolicyQueryPort routeQueryPort,
        ChatRouteResolver routeResolver,
        IScopeResourceAdmissionPort admissionPort,
        IChatHistoryStore chatHistoryStore,
        ILogger<NyxIdChatLifecycleFacade> logger)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _registryCommandPort = registryCommandPort ?? throw new ArgumentNullException(nameof(registryCommandPort));
        _routeQueryPort = routeQueryPort ?? throw new ArgumentNullException(nameof(routeQueryPort));
        _routeResolver = routeResolver ?? throw new ArgumentNullException(nameof(routeResolver));
        _admissionPort = admissionPort ?? throw new ArgumentNullException(nameof(admissionPort));
        _chatHistoryStore = chatHistoryStore ?? throw new ArgumentNullException(nameof(chatHistoryStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NyxIdChatConversationCreateReceipt> CreateConversationAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var callerScope = OwnerScope.ForNyxIdNative(normalizedScopeId);
        var snapshot = await _routeQueryPort.LookupForCallerAsync(callerScope, ct);
        var decision = _routeResolver.Resolve(snapshot, new ChatRouteInput
        {
            SourceKind = ChatSourceKind.Direct,
            CallerScope = ToChatRouteCallerScope(callerScope),
            Channel = string.Empty,
            CommandName = string.Empty,
            ContentHint = string.Empty,
            ToolMode = ToolMode.None,
        });

        if (decision.Action.Reject is { } reject)
            return new NyxIdChatConversationCreateReceipt(
                NyxIdChatConversationCreateStatus.RouteRejected,
                null,
                reject);

        var forwardedActorId = decision.Action.ForwardToGagent?.ActorId;
        var actorId = !string.IsNullOrWhiteSpace(forwardedActorId)
            ? forwardedActorId.Trim()
            : NyxIdChatServiceDefaults.GenerateActorId();
        var createdLocally = string.IsNullOrWhiteSpace(forwardedActorId);

        if (createdLocally)
            await _actorRuntime.CreateAsync<NyxIdChatGAgent>(actorId, ct);

        try
        {
            var receipt = await _registryCommandPort.RegisterActorAsync(
                new GAgentActorRegistration(normalizedScopeId, NyxIdChatServiceDefaults.GAgentTypeName, actorId),
                ct);
            if (receipt.IsAdmissionVisible)
            {
                return new NyxIdChatConversationCreateReceipt(
                    NyxIdChatConversationCreateStatus.Accepted,
                    actorId,
                    null);
            }

            await RequestCreationCompensationAsync(
                normalizedScopeId,
                actorId,
                createdLocally,
                "registration_not_admission_visible",
                CancellationToken.None);
            return new NyxIdChatConversationCreateReceipt(
                NyxIdChatConversationCreateStatus.RegistrationUnavailable,
                actorId,
                null);
        }
        catch
        {
            await RequestCreationCompensationAsync(
                normalizedScopeId,
                actorId,
                createdLocally,
                "registration_failed",
                CancellationToken.None);
            throw;
        }
    }

    public async Task<NyxIdChatConversationDeleteReceipt> DeleteConversationAsync(
        string scopeId,
        string actorId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = NormalizeRequired(scopeId, nameof(scopeId));
        var normalizedActorId = NormalizeRequired(actorId, nameof(actorId));
        var admission = await _admissionPort.AuthorizeTargetAsync(
            new ScopeResourceTarget(
                normalizedScopeId,
                ScopeResourceKind.GAgentActor,
                NyxIdChatServiceDefaults.GAgentTypeName,
                normalizedActorId,
                ScopeResourceOperation.Delete),
            ct);

        if (!admission.IsAllowed)
            return new NyxIdChatConversationDeleteReceipt(MapDeleteAdmission(admission.Status));

        await _registryCommandPort.UnregisterActorAsync(
            new GAgentActorRegistration(normalizedScopeId, NyxIdChatServiceDefaults.GAgentTypeName, normalizedActorId),
            ct);

        try
        {
            await _chatHistoryStore.DeleteConversationAsync(normalizedScopeId, normalizedActorId, ct);
        }
        catch
        {
            await RequestDeletionCompensationAsync(
                normalizedScopeId,
                normalizedActorId,
                "history_delete_failed",
                CancellationToken.None);
            throw;
        }

        return new NyxIdChatConversationDeleteReceipt(NyxIdChatConversationDeleteStatus.Accepted);
    }

    private async Task RequestCreationCompensationAsync(
        string scopeId,
        string actorId,
        bool destroyActor,
        string reason,
        CancellationToken ct)
    {
        try
        {
            await _dispatchPort.DispatchAsync(
                actorId,
                CreateDirectEnvelope(
                    actorId,
                    new NyxIdChatConversationCreationCompensationRequested
                    {
                        ScopeId = scopeId,
                        ActorId = actorId,
                        DestroyActor = destroyActor,
                        Reason = reason,
                    }),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to dispatch NyxID chat creation compensation: scope={ScopeId}, actor={ActorId}",
                scopeId,
                actorId);
        }
    }

    private async Task RequestDeletionCompensationAsync(
        string scopeId,
        string actorId,
        string reason,
        CancellationToken ct)
    {
        try
        {
            await _dispatchPort.DispatchAsync(
                actorId,
                CreateDirectEnvelope(
                    actorId,
                    new NyxIdChatConversationDeletionCompensationRequested
                    {
                        ScopeId = scopeId,
                        ActorId = actorId,
                        Reason = reason,
                    }),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to dispatch NyxID chat deletion compensation: scope={ScopeId}, actor={ActorId}",
                scopeId,
                actorId);
        }
    }

    internal static ChatRouteCallerScope ToChatRouteCallerScope(OwnerScope scope) => new()
    {
        NyxUserId = scope.NyxUserId,
        Platform = scope.Platform,
        RegistrationScopeId = scope.RegistrationScopeId,
        SenderId = scope.SenderId,
    };

    private static NyxIdChatConversationDeleteStatus MapDeleteAdmission(ScopeResourceAdmissionStatus status) =>
        status switch
        {
            ScopeResourceAdmissionStatus.NotFound => NyxIdChatConversationDeleteStatus.NotFound,
            ScopeResourceAdmissionStatus.Denied or ScopeResourceAdmissionStatus.ScopeMismatch =>
                NyxIdChatConversationDeleteStatus.AccessDenied,
            _ => NyxIdChatConversationDeleteStatus.AdmissionUnavailable,
        };

    private static EventEnvelope CreateDirectEnvelope(string actorId, IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute
                {
                    TargetActorId = actorId,
                },
            },
        };

    private static string NormalizeRequired(string value, string parameterName)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        return normalized;
    }
}
