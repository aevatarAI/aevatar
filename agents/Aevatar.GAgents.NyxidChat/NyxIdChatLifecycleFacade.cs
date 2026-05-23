using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Hosting;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

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

public sealed record NyxIdChatLifecycleCommandReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId,
    NyxIdChatConversationCreateStatus CreateStatus,
    NyxIdChatConversationDeleteStatus DeleteStatus,
    Reject? Reject = null);

public enum NyxIdChatLifecycleCommandStartError
{
    None = 0,
    RouteRejected = 1,
    AdmissionUnavailable = 2,
    TargetNotFound = 3,
}

public sealed class NyxIdChatLifecycleFacade
{
    private readonly ICommandDispatchService<NyxIdChatConversationCreateCommand, NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError> _createDispatchService;
    private readonly ICommandDispatchService<NyxIdChatConversationDeleteCommand, NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError> _deleteDispatchService;

    public NyxIdChatLifecycleFacade(
        ICommandDispatchService<NyxIdChatConversationCreateCommand, NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError> createDispatchService,
        ICommandDispatchService<NyxIdChatConversationDeleteCommand, NyxIdChatLifecycleCommandReceipt, NyxIdChatLifecycleCommandStartError> deleteDispatchService)
    {
        _createDispatchService = createDispatchService ?? throw new ArgumentNullException(nameof(createDispatchService));
        _deleteDispatchService = deleteDispatchService ?? throw new ArgumentNullException(nameof(deleteDispatchService));
    }

    public async Task<NyxIdChatConversationCreateReceipt> CreateConversationAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var result = await _createDispatchService.DispatchAsync(
            new NyxIdChatConversationCreateCommand
            {
                ScopeId = NormalizeRequired(scopeId, nameof(scopeId)),
            },
            ct);

        if (result.Succeeded && result.Receipt is not null)
        {
            return new NyxIdChatConversationCreateReceipt(
                result.Receipt.CreateStatus,
                result.Receipt.ActorId,
                result.Receipt.Reject);
        }

        return result.Error switch
        {
            NyxIdChatLifecycleCommandStartError.RouteRejected =>
                new NyxIdChatConversationCreateReceipt(NyxIdChatConversationCreateStatus.RouteRejected, null, null),
            NyxIdChatLifecycleCommandStartError.TargetNotFound =>
                new NyxIdChatConversationCreateReceipt(NyxIdChatConversationCreateStatus.RegistrationUnavailable, null, null),
            _ => new NyxIdChatConversationCreateReceipt(NyxIdChatConversationCreateStatus.RegistrationUnavailable, null, null),
        };
    }

    public async Task<NyxIdChatConversationDeleteReceipt> DeleteConversationAsync(
        string scopeId,
        string actorId,
        CancellationToken ct = default)
    {
        var result = await _deleteDispatchService.DispatchAsync(
            new NyxIdChatConversationDeleteCommand
            {
                ScopeId = NormalizeRequired(scopeId, nameof(scopeId)),
                ActorId = NormalizeRequired(actorId, nameof(actorId)),
            },
            ct);

        if (result.Succeeded && result.Receipt is not null)
            return new NyxIdChatConversationDeleteReceipt(result.Receipt.DeleteStatus);

        return new NyxIdChatConversationDeleteReceipt(NyxIdChatConversationDeleteStatus.AdmissionUnavailable);
    }

    private static string NormalizeRequired(string value, string parameterName)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        return normalized;
    }
}

internal sealed class NyxIdChatConversationCreateCommandTarget
    : IActorCommandDispatchTarget
{
    public NyxIdChatConversationCreateCommandTarget(
        IActor actor,
        NyxIdChatConversationCreateStatus status,
        Reject? reject = null)
    {
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        Status = status;
        Reject = reject;
    }

    public IActor Actor { get; }
    public string TargetId => Actor.Id;
    public NyxIdChatConversationCreateStatus Status { get; }
    public Reject? Reject { get; }
}

internal sealed class NyxIdChatConversationDeleteCommandTarget
    : IActorCommandDispatchTarget
{
    public NyxIdChatConversationDeleteCommandTarget(
        IActor actor,
        NyxIdChatConversationDeleteStatus status)
    {
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        Status = status;
    }

    public IActor Actor { get; }
    public string TargetId => Actor.Id;
    public NyxIdChatConversationDeleteStatus Status { get; }
}

internal sealed class NyxIdChatConversationCreateCommandTargetResolver
    : ICommandTargetResolver<NyxIdChatConversationCreateCommand, NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandStartError>
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IChatRoutePolicyQueryPort _routeQueryPort;
    private readonly ChatRouteResolver _routeResolver;

    public NyxIdChatConversationCreateCommandTargetResolver(
        IActorRuntime actorRuntime,
        IChatRoutePolicyQueryPort routeQueryPort,
        ChatRouteResolver routeResolver)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _routeQueryPort = routeQueryPort ?? throw new ArgumentNullException(nameof(routeQueryPort));
        _routeResolver = routeResolver ?? throw new ArgumentNullException(nameof(routeResolver));
    }

    public async Task<CommandTargetResolution<NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandStartError>> ResolveAsync(
        NyxIdChatConversationCreateCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var callerScope = OwnerScope.ForNyxIdNative(command.ScopeId);
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

        if (decision.Action.Reject is not null)
            return CommandTargetResolution<NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandStartError>.Failure(
                NyxIdChatLifecycleCommandStartError.RouteRejected);

        var forwardedActorId = decision.Action.ForwardToGagent?.ActorId;
        if (!string.IsNullOrWhiteSpace(forwardedActorId))
        {
            var forwardedActor = await _actorRuntime.GetAsync(forwardedActorId.Trim());
            if (forwardedActor is null)
                return CommandTargetResolution<NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandStartError>.Failure(
                    NyxIdChatLifecycleCommandStartError.TargetNotFound);

            return CommandTargetResolution<NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandStartError>.Success(
                new NyxIdChatConversationCreateCommandTarget(forwardedActor, NyxIdChatConversationCreateStatus.Accepted));
        }

        var actorId = NyxIdChatServiceDefaults.GenerateActorId();
        var createdActor = await _actorRuntime.CreateAsync<NyxIdChatGAgent>(actorId, ct);
        return CommandTargetResolution<NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandStartError>.Success(
            new NyxIdChatConversationCreateCommandTarget(createdActor, NyxIdChatConversationCreateStatus.Accepted));
    }

    private static ChatRouteCallerScope ToChatRouteCallerScope(OwnerScope scope) => new()
    {
        NyxUserId = scope.NyxUserId,
        Platform = scope.Platform,
        RegistrationScopeId = scope.RegistrationScopeId,
        SenderId = scope.SenderId,
    };
}

internal sealed class NyxIdChatConversationDeleteCommandTargetResolver
    : ICommandTargetResolver<NyxIdChatConversationDeleteCommand, NyxIdChatConversationDeleteCommandTarget, NyxIdChatLifecycleCommandStartError>
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IScopeResourceAdmissionPort _admissionPort;

    public NyxIdChatConversationDeleteCommandTargetResolver(
        IActorRuntime actorRuntime,
        IScopeResourceAdmissionPort admissionPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _admissionPort = admissionPort ?? throw new ArgumentNullException(nameof(admissionPort));
    }

    public async Task<CommandTargetResolution<NyxIdChatConversationDeleteCommandTarget, NyxIdChatLifecycleCommandStartError>> ResolveAsync(
        NyxIdChatConversationDeleteCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var admission = await _admissionPort.AuthorizeTargetAsync(
            new ScopeResourceTarget(
                command.ScopeId,
                ScopeResourceKind.GAgentActor,
                NyxIdChatServiceDefaults.GAgentTypeName,
                command.ActorId,
                ScopeResourceOperation.Delete),
            ct);

        var status = MapDeleteAdmission(admission.Status);
        var actor = await _actorRuntime.GetAsync(command.ActorId);
        if (actor is null)
            return CommandTargetResolution<NyxIdChatConversationDeleteCommandTarget, NyxIdChatLifecycleCommandStartError>.Failure(
                NyxIdChatLifecycleCommandStartError.TargetNotFound);

        return CommandTargetResolution<NyxIdChatConversationDeleteCommandTarget, NyxIdChatLifecycleCommandStartError>.Success(
            new NyxIdChatConversationDeleteCommandTarget(actor, status));
    }

    private static NyxIdChatConversationDeleteStatus MapDeleteAdmission(ScopeResourceAdmissionStatus status) =>
        status switch
        {
            ScopeResourceAdmissionStatus.Allowed => NyxIdChatConversationDeleteStatus.Accepted,
            ScopeResourceAdmissionStatus.NotFound => NyxIdChatConversationDeleteStatus.NotFound,
            ScopeResourceAdmissionStatus.Denied or ScopeResourceAdmissionStatus.ScopeMismatch =>
                NyxIdChatConversationDeleteStatus.AccessDenied,
            _ => NyxIdChatConversationDeleteStatus.AdmissionUnavailable,
        };
}

internal sealed class NyxIdChatLifecycleCommandEnvelopeFactory :
    ICommandEnvelopeFactory<NyxIdChatConversationCreateCommand>,
    ICommandEnvelopeFactory<NyxIdChatConversationDeleteCommand>
{
    public EventEnvelope CreateEnvelope(NyxIdChatConversationCreateCommand command, CommandContext context) =>
        CreateDirectEnvelope(command, context);

    public EventEnvelope CreateEnvelope(NyxIdChatConversationDeleteCommand command, CommandContext context) =>
        CreateDirectEnvelope(command, context);

    private static EventEnvelope CreateDirectEnvelope(IMessage command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return new EventEnvelope
        {
            Id = context.CommandId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = context.TargetId } },
            Propagation = new EnvelopePropagation { CorrelationId = context.CorrelationId },
        };
    }
}

internal sealed class NyxIdChatCreateLifecycleCommandReceiptFactory
    : ICommandReceiptFactory<NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandReceipt>
{
    public NyxIdChatLifecycleCommandReceipt Create(
        NyxIdChatConversationCreateCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new NyxIdChatLifecycleCommandReceipt(
            target.Actor.Id,
            context.CommandId,
            context.CorrelationId,
            target.Status,
            NyxIdChatConversationDeleteStatus.Accepted,
            target.Reject);
    }
}

internal sealed class NyxIdChatDeleteLifecycleCommandReceiptFactory
    : ICommandReceiptFactory<NyxIdChatConversationDeleteCommandTarget, NyxIdChatLifecycleCommandReceipt>
{
    public NyxIdChatLifecycleCommandReceipt Create(
        NyxIdChatConversationDeleteCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new NyxIdChatLifecycleCommandReceipt(
            target.Actor.Id,
            context.CommandId,
            context.CorrelationId,
            NyxIdChatConversationCreateStatus.Accepted,
            target.Status);
    }
}
