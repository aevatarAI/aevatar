using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Capabilities;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

public sealed record NyxIdChatConversationCreateReceipt(
    NyxIdChatConversationCreateStatus Status,
    string? ActorId,
    Reject? Reject,
    string? CommandId = null,
    string? CorrelationId = null);

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
    Reject? Reject = null);

public enum NyxIdChatLifecycleCommandStartError
{
    None = 0,
    RouteRejected = 1,
    AdmissionUnavailable = 2,
    TargetNotFound = 3,
    AccessDenied = 4,
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
        // Refactor (iter77/cluster-077-cqrs-command-outcome-stream-rpc):
        //   Old pattern: NyxIdChat create awaited actor outcome via stream-RPC primitive (DispatchAndAwaitOutcomeAsync)
        //   New principle (narrow scope): NyxIdChat create returns honest accepted ACK; terminal facts via committed events
        var result = await _createDispatchService.DispatchAsync(
            new NyxIdChatConversationCreateCommand
            {
                ScopeId = NormalizeRequired(scopeId, nameof(scopeId)),
            },
            ct);

        if (result.Succeeded && result.Receipt is not null)
        {
            return new NyxIdChatConversationCreateReceipt(
                NyxIdChatConversationCreateStatus.Accepted,
                result.Receipt.ActorId,
                result.Receipt.Reject,
                result.Receipt.CommandId,
                result.Receipt.CorrelationId);
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
            return new NyxIdChatConversationDeleteReceipt(NyxIdChatConversationDeleteStatus.Accepted);

        return result.Error switch
        {
            NyxIdChatLifecycleCommandStartError.TargetNotFound =>
                new NyxIdChatConversationDeleteReceipt(NyxIdChatConversationDeleteStatus.NotFound),
            NyxIdChatLifecycleCommandStartError.AccessDenied =>
                new NyxIdChatConversationDeleteReceipt(NyxIdChatConversationDeleteStatus.AccessDenied),
            _ => new NyxIdChatConversationDeleteReceipt(NyxIdChatConversationDeleteStatus.AdmissionUnavailable),
        };
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
        bool createdLocally,
        NyxIdChatConversationCreateStatus status,
        Reject? reject = null)
    {
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        CreatedLocally = createdLocally;
        Status = status;
        Reject = reject;
    }

    public IActor Actor { get; }
    public string TargetId => Actor.Id;
    public bool CreatedLocally { get; }
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
    private readonly INyxIdChatAgentProfileResolver _agentProfileResolver;

    public NyxIdChatConversationCreateCommandTargetResolver(
        IActorRuntime actorRuntime,
        IChatRoutePolicyQueryPort routeQueryPort,
        ChatRouteResolver routeResolver,
        INyxIdChatAgentProfileResolver agentProfileResolver)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _routeQueryPort = routeQueryPort ?? throw new ArgumentNullException(nameof(routeQueryPort));
        _routeResolver = routeResolver ?? throw new ArgumentNullException(nameof(routeResolver));
        _agentProfileResolver = agentProfileResolver ??
                                throw new ArgumentNullException(nameof(agentProfileResolver));
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
            CallerScope = callerScope.Clone(),
            Channel = string.Empty,
            CommandName = string.Empty,
            ContentHint = string.Empty,
            ToolMode = ToolMode.None,
        });

        if (decision.Action.Reject is not null)
            return CommandTargetResolution<NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandStartError>.Failure(
                NyxIdChatLifecycleCommandStartError.RouteRejected);

        var actorId = string.IsNullOrWhiteSpace(command.RequestedActorId)
            ? NyxIdChatServiceDefaults.GenerateActorId()
            : command.RequestedActorId.Trim();
        var profileResolution = await _agentProfileResolver.ResolveAsync(
            new NyxIdChatAgentProfileSelectionRequest(
                command.ScopeId.Trim(),
                actorId,
                command.AgentProfileReference?.Clone()),
            ct);
        if (profileResolution.IsFailure)
        {
            return CommandTargetResolution<NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandStartError>.Failure(
                NyxIdChatLifecycleCommandStartError.AdmissionUnavailable);
        }

        var agentProfile = profileResolution.Profile;
        if (profileResolution.IsSelected)
        {
            if (agentProfile is null || !AgentProfileSnapshotCodec.Verify(agentProfile))
            {
                return CommandTargetResolution<NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandStartError>.Failure(
                    NyxIdChatLifecycleCommandStartError.AdmissionUnavailable);
            }

            var routeToolSetName = decision.Action.ForwardToModel?.ToolSetRef?.Name;
            if (!string.Equals(routeToolSetName, agentProfile.RouteToolSetRef, StringComparison.Ordinal))
            {
                return CommandTargetResolution<NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandStartError>.Failure(
                    NyxIdChatLifecycleCommandStartError.AdmissionUnavailable);
            }

            command.AgentProfile = agentProfile.Clone();
        }

        // Refactor (issue1321-first): ForwardToModel.tool_choice_hint is tool prefill only,
        // so conversation creation never treats hint arguments as actor addressing.

        var createdActor = await _actorRuntime.CreateAsync<NyxIdChatConversationGAgent>(actorId, ct);
        command.CreatedLocally = true;
        return CommandTargetResolution<NyxIdChatConversationCreateCommandTarget, NyxIdChatLifecycleCommandStartError>.Success(
            new NyxIdChatConversationCreateCommandTarget(
                createdActor,
                createdLocally: true,
                NyxIdChatConversationCreateStatus.Accepted));
    }
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
                NyxIdChatServiceDefaults.GAgentKind,
                command.ActorId,
                ScopeResourceOperation.Delete),
            ct);

        var status = MapDeleteAdmission(admission.Status);
        if (status != NyxIdChatConversationDeleteStatus.Accepted)
            return CommandTargetResolution<NyxIdChatConversationDeleteCommandTarget, NyxIdChatLifecycleCommandStartError>.Failure(
                status == NyxIdChatConversationDeleteStatus.NotFound
                    ? NyxIdChatLifecycleCommandStartError.TargetNotFound
                    : status == NyxIdChatConversationDeleteStatus.AccessDenied
                        ? NyxIdChatLifecycleCommandStartError.AccessDenied
                        : NyxIdChatLifecycleCommandStartError.AdmissionUnavailable);

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
            context.CorrelationId);
    }
}
