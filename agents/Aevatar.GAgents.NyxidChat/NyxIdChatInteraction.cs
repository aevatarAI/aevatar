using System.Runtime.ExceptionServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.SkillInvocations;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.AGUI.Contracts;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.NyxidChat;

public sealed record NyxIdChatCommand(
    string ActorId,
    string ScopeId,
    string Prompt,
    string TurnId,
    string AccessToken,
    IReadOnlyList<ChatContentPart>? InputParts,
    IReadOnlyDictionary<string, string>? Metadata,
    LLMControlContext? LlmControl = null,
    string? CommandId = null,
    string? CorrelationId = null,
    string? ClientRequestId = null,
    bool CreateIfMissing = false,
    string? OwnerSubject = null,
    AgentProfileReference? AgentProfileReference = null,
    AgentToolNyxIdCredentialKind NyxIdCredentialKind =
        AgentToolNyxIdCredentialKind.Unspecified,
    string? InputPartsFingerprint = null,
    IReadOnlyList<ConversationContextAttachment>? ContextAttachments = null)
    : ICommandContextSeed
{
    public IReadOnlyDictionary<string, string>? Headers => null;

    internal bool CreatedLocally { get; set; }

    internal AgentProfileSnapshot? AgentProfile { get; set; }
}

internal static class NyxIdChatPublicIdentity
{
    public static string CreateConversationActorId(string scopeId, string clientRequestId) =>
        Build("nyxid-chat", scopeId.Trim(), clientRequestId.Trim());

    public static string CreateTurnId(string actorId, string clientRequestId) =>
        Build("turn", actorId.Trim(), clientRequestId.Trim());

    public static string CreateInputPartsFingerprint(IEnumerable<ChatContentPart> inputParts)
    {
        var payload = new ChatRequestEvent();
        payload.InputParts.Add(inputParts.Select(static part => part.Clone()));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            payload.ToByteArray()));
    }

    public static string CreateChatCommandId(
        string actorId,
        string scopeId,
        string ownerSubject,
        string? clientRequestId,
        string turnId,
        string prompt,
        IEnumerable<Aevatar.AI.Abstractions.ChatContentPart> inputParts,
        AgentProfileReference? agentProfileReference,
        IEnumerable<ConversationContextAttachment>? contextAttachments = null)
    {
        var payload = new NyxIdChatStartTurnCommand
        {
            ScopeId = scopeId.Trim(),
            ConversationActorId = actorId.Trim(),
            TurnId = turnId.Trim(),
            ClientRequestId = clientRequestId?.Trim() ?? string.Empty,
            Prompt = prompt,
        };
        payload.InputParts.Add(inputParts.Select(static part => part.Clone()));
        payload.ContextAttachments = ConversationContextAttachmentAdmission.CloneOptionalSet(contextAttachments);

        return Build(
            "command",
            ownerSubject.Trim(),
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                payload.ToByteArray())),
            agentProfileReference is null
                ? string.Empty
                : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                    agentProfileReference.ToByteArray())));
    }

    public static string CreateActionContinuationCommandId(
        string actorId,
        string scopeId,
        string ownerSubject,
        string clientRequestId,
        string originTurnId,
        IEnumerable<NyxIdChatActionReport> actions)
    {
        var payload = new NyxIdChatActionContinueCommand
        {
            ScopeId = scopeId.Trim(),
            ConversationActorId = actorId.Trim(),
            OriginTurnId = originTurnId.Trim(),
            OwnerSubject = ownerSubject.Trim(),
            ClientRequestId = clientRequestId.Trim(),
        };
        payload.Actions.Add(actions
            .OrderBy(static action => action.ActionRequestId, StringComparer.Ordinal)
            .Select(static action =>
            {
                var canonical = new NyxIdChatActionReport
                {
                    ActionRequestId = action.ActionRequestId.Trim(),
                    OriginTurnId = action.OriginTurnId.Trim(),
                    Disposition = action.Disposition,
                };
                if (action.Resource is not null)
                    canonical.Resource = action.Resource.Clone();
                return canonical;
            }));

        return Build(
            "command",
            actorId.Trim(),
            scopeId.Trim(),
            ownerSubject.Trim(),
            clientRequestId.Trim(),
            originTurnId.Trim(),
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                payload.ToByteArray())));
    }

    private static string Build(string prefix, params string[] parts)
    {
        var identity = string.Concat(parts.Select(static part => $"{part.Length}:{part}"));
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity));
        return $"{prefix}-{Convert.ToHexStringLower(hash)[..32]}";
    }
}

public sealed record NyxIdApprovalCommand(
    string ActorId,
    string RequestId,
    bool Approved,
    string Reason,
    string TurnId,
    string? CommandId = null,
    string? CorrelationId = null)
    : ICommandContextSeed
{
    public IReadOnlyDictionary<string, string>? Headers => null;
}

public sealed record NyxIdActionContinuationCommand(
    string ActorId,
    string ScopeId,
    string OriginTurnId,
    string ContinuationTurnId,
    string OwnerSubject,
    string ClientRequestId,
    IReadOnlyList<NyxIdChatActionReport> Actions,
    string? CommandId = null,
    string? CorrelationId = null,
    AgentToolExecutionContextPayload? ToolContext = null)
    : ICommandContextSeed
{
    public IReadOnlyDictionary<string, string>? Headers => null;
}

// Refactor (iter21/cluster-002-request-path-projection-session-priming):
//   Old pattern: streaming endpoints implied completion from local live-sink progress.
//   New principle: accepted receipts expose only dispatch identity; completion is observed separately.
public sealed record NyxIdChatAcceptedReceipt(
    string ActorId,
    string CommandId,
    string CorrelationId,
    string TurnId,
    string ScopeId = "");

public enum NyxIdChatStartError
{
    None = 0,
    ActorNotFound = 1,
    ProjectionUnavailable = 2,
    AdmissionUnavailable = 3,
}

public readonly record struct NyxIdChatCompletionStatus
{
    private NyxIdChatCompletionStatus(int value, AGUIEvent? durableTerminal = null)
    {
        Value = value;
        DurableTerminal = durableTerminal;
    }

    public static NyxIdChatCompletionStatus Unknown { get; } = new(0);
    public static NyxIdChatCompletionStatus Completed { get; } = new(1);
    public static NyxIdChatCompletionStatus Failed { get; } = new(2);

    internal int Value { get; }
    internal AGUIEvent? DurableTerminal { get; }

    public bool Equals(NyxIdChatCompletionStatus other) => Value == other.Value;

    public override int GetHashCode() => Value;

    internal NyxIdChatCompletionStatus WithDurableTerminal(AGUIEvent terminal) =>
        new(Value, terminal);
}

internal sealed class NyxIdChatCommandTarget
    : IActorCommandDispatchTarget,
      ICommandEventTarget<AGUIEvent>,
      ICommandInteractionCleanupTarget<NyxIdChatAcceptedReceipt, NyxIdChatCompletionStatus>,
      ICommandDispatchCleanupAware
{
    private readonly INyxIdChatSessionProjectionPort _projectionPort;

    public NyxIdChatCommandTarget(
        IActor actor,
        INyxIdChatSessionProjectionPort projectionPort)
    {
        // Refactor (iter21/cluster-002-request-path-projection-session-priming):
        //   Old pattern: request handlers synchronously ensure projection/session leases and wait on live sinks.
        //   New principle: commands use accepted receipts; observation is owned by binders or attach-only sessions.
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public IActor Actor { get; }
    public string TargetId => Actor.Id;
    public string ActorId => Actor.Id;
    public string TurnId { get; private set; } = string.Empty;
    public string ScopeId { get; private set; } = string.Empty;
    public INyxIdChatSessionProjectionLease? ProjectionLease { get; private set; }
    public IAsyncDisposable? LiveSinkLease { get; private set; }
    public IEventSink<AGUIEvent>? LiveSink { get; private set; }

    public void BindLiveObservation(
        INyxIdChatSessionProjectionLease projectionLease,
        IAsyncDisposable? liveSinkLease,
        IEventSink<AGUIEvent> sink,
        string turnId)
    {
        // Refactor (iter37/cluster-037-agent-session-observation-attach-only):
        //   Old pattern: Agent session observation binders 同步 prime projection lease before dispatch(NyxID/StreamingProxy session paths)。
        //   New principle: Attach-existing NyxID/StreamingProxy observation ports;cold sessions return ProjectionUnavailable before dispatch;projection activation 移到 projection-owned lifecycle;不引入新 actor / 新 envelope / CLAUDE 例外。
        ProjectionLease = projectionLease ?? throw new ArgumentNullException(nameof(projectionLease));
        LiveSinkLease = liveSinkLease;
        LiveSink = sink ?? throw new ArgumentNullException(nameof(sink));
        TurnId = string.IsNullOrWhiteSpace(turnId)
            ? throw new ArgumentException("Turn id is required.", nameof(turnId))
            : turnId;
    }

    public void BindScope(string scopeId)
    {
        ScopeId = string.IsNullOrWhiteSpace(scopeId)
            ? throw new ArgumentException("Scope id is required.", nameof(scopeId))
            : scopeId.Trim();
    }

    public IEventSink<AGUIEvent> RequireLiveSink() =>
        LiveSink ?? throw new InvalidOperationException("NyxID chat live sink is not bound.");

    public Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default) =>
        ReleaseAsync(ct);

    public Task ReleaseAfterInteractionAsync(
        NyxIdChatAcceptedReceipt receipt,
        CommandInteractionCleanupContext<NyxIdChatCompletionStatus> cleanup,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(cleanup);
        return ReleaseAsync(ct);
    }

    private async Task ReleaseAsync(CancellationToken ct)
    {
        Exception? firstException = null;
        var projectionLease = ProjectionLease;
        var sink = LiveSink;

        if (projectionLease != null && sink != null)
        {
            try
            {
                await _projectionPort.DetachReleaseAndDisposeAsync(
                    projectionLease,
                    LiveSinkLease,
                    sink,
                    null,
                    ct);
                ProjectionLease = null;
                LiveSinkLease = null;
                LiveSink = null;
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }
        else if (sink != null)
        {
            try
            {
                sink.Complete();
                await sink.DisposeAsync();
                LiveSinkLease = null;
                LiveSink = null;
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }

        if (firstException != null)
            ExceptionDispatchInfo.Capture(firstException).Throw();
    }
}

internal sealed class NyxIdChatCommandTargetResolver<TCommand>
    : ICommandTargetResolver<TCommand, NyxIdChatCommandTarget, NyxIdChatStartError>
{
    private readonly IActorRuntime _actorRuntime;
    private readonly INyxIdChatSessionProjectionPort _projectionPort;
    private readonly Func<TCommand, string> _actorIdResolver;

    public NyxIdChatCommandTargetResolver(
        IActorRuntime actorRuntime,
        INyxIdChatSessionProjectionPort projectionPort,
        Func<TCommand, string> actorIdResolver)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _actorIdResolver = actorIdResolver ?? throw new ArgumentNullException(nameof(actorIdResolver));
    }

    public async Task<CommandTargetResolution<NyxIdChatCommandTarget, NyxIdChatStartError>> ResolveAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var actor = await _actorRuntime.GetAsync(_actorIdResolver(command));
        if (actor == null)
        {
            return CommandTargetResolution<NyxIdChatCommandTarget, NyxIdChatStartError>.Failure(
                NyxIdChatStartError.ActorNotFound);
        }

        return CommandTargetResolution<NyxIdChatCommandTarget, NyxIdChatStartError>.Success(
            new NyxIdChatCommandTarget(actor, _projectionPort));
    }
}

internal sealed class NyxIdChatCommandTargetResolver
    : ICommandTargetResolver<NyxIdChatCommand, NyxIdChatCommandTarget, NyxIdChatStartError>
{
    private readonly IActorRuntime _actorRuntime;
    private readonly INyxIdChatSessionProjectionPort _projectionPort;
    private readonly Func<ICommandTargetResolver<
        NyxIdChatConversationCreateCommand,
        NyxIdChatConversationCreateCommandTarget,
        NyxIdChatLifecycleCommandStartError>> _createResolver;

    public NyxIdChatCommandTargetResolver(
        IActorRuntime actorRuntime,
        INyxIdChatSessionProjectionPort projectionPort,
        Func<ICommandTargetResolver<
            NyxIdChatConversationCreateCommand,
            NyxIdChatConversationCreateCommandTarget,
            NyxIdChatLifecycleCommandStartError>> createResolver)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _createResolver = createResolver ?? throw new ArgumentNullException(nameof(createResolver));
    }

    public async Task<CommandTargetResolution<NyxIdChatCommandTarget, NyxIdChatStartError>> ResolveAsync(
        NyxIdChatCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!command.CreateIfMissing)
        {
            var existing = await _actorRuntime.GetAsync(command.ActorId);
            return existing is null
                ? CommandTargetResolution<NyxIdChatCommandTarget, NyxIdChatStartError>.Failure(
                    NyxIdChatStartError.ActorNotFound)
                : CommandTargetResolution<NyxIdChatCommandTarget, NyxIdChatStartError>.Success(
                    new NyxIdChatCommandTarget(existing, _projectionPort));
        }

        var create = new NyxIdChatConversationCreateCommand
        {
            ScopeId = command.ScopeId,
            RequestedActorId = command.ActorId,
            AgentProfileReference = command.AgentProfileReference?.Clone(),
            ContextAttachments = ConversationContextAttachmentAdmission.CloneOptionalSet(command.ContextAttachments),
            FirstTurn = new NyxIdChatStartTurnCommand
            {
                ToolContext = new AgentToolExecutionContextPayload
                {
                    Caller = new AgentToolCallerContextPayload
                    {
                        OwnerSubject = command.OwnerSubject?.Trim() ?? string.Empty,
                    },
                },
            },
        };
        var resolved = await _createResolver().ResolveAsync(create, ct);
        if (!resolved.Succeeded || resolved.Target is null)
        {
            return CommandTargetResolution<NyxIdChatCommandTarget, NyxIdChatStartError>.Failure(
                resolved.Error switch
                {
                    NyxIdChatLifecycleCommandStartError.TargetNotFound => NyxIdChatStartError.ActorNotFound,
                    NyxIdChatLifecycleCommandStartError.AdmissionUnavailable or
                        NyxIdChatLifecycleCommandStartError.RouteRejected or
                        NyxIdChatLifecycleCommandStartError.AccessDenied => NyxIdChatStartError.AdmissionUnavailable,
                    _ => NyxIdChatStartError.ProjectionUnavailable,
                });
        }

        command.CreatedLocally = resolved.Target.CreatedLocally;
        command.AgentProfile = create.AgentProfile?.Clone();
        return CommandTargetResolution<NyxIdChatCommandTarget, NyxIdChatStartError>.Success(
            new NyxIdChatCommandTarget(resolved.Target.Actor, _projectionPort));
    }
}

internal sealed class NyxIdChatObservationLifecycle<TCommand>
    : ICommandObservationLifecycle<TCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError>
{
    // Refactor (iter37/cluster-037-agent-session-observation-attach-only):
    //   Old pattern: Agent session observation binders 同步 prime projection lease before dispatch(NyxID/StreamingProxy session paths)。
    //   New principle: Attach-existing NyxID/StreamingProxy observation ports;cold sessions return ProjectionUnavailable before dispatch;projection activation 移到 projection-owned lifecycle;不引入新 actor / 新 envelope / CLAUDE 例外。
    private readonly INyxIdChatSessionProjectionPort _projectionPort;
    private readonly Func<TCommand, string> _turnIdResolver;

    public NyxIdChatObservationLifecycle(
        INyxIdChatSessionProjectionPort projectionPort,
        Func<TCommand, string> turnIdResolver)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _turnIdResolver = turnIdResolver ?? throw new ArgumentNullException(nameof(turnIdResolver));
    }

    public async Task<CommandObservationBindingResult<NyxIdChatStartError>> BindAsync(
        TCommand command,
        CommandDispatchExecution<NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt> execution,
        CancellationToken ct = default)
    {
        // Refactor (iter37/cluster-037-agent-session-observation-attach-only):
        //   Old pattern: Agent session observation binders 同步 prime projection lease before dispatch(NyxID/StreamingProxy session paths)。
        //   New principle: Attach-existing NyxID/StreamingProxy observation ports;cold sessions return ProjectionUnavailable before dispatch;projection activation 移到 projection-owned lifecycle;不引入新 actor / 新 envelope / CLAUDE 例外。
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execution);

        var target = execution.Target;
        var scopeId = command switch
        {
            NyxIdChatCommand chat => chat.ScopeId,
            NyxIdActionContinuationCommand continuation => continuation.ScopeId,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(scopeId))
            target.BindScope(scopeId);
        var turnId = _turnIdResolver(command);
        var sink = new EventChannel<AGUIEvent>();
        try
        {
            var attachment = await _projectionPort.AttachExistingChatProjectionAsync(
                target.ActorId,
                turnId,
                sink,
                ct);
            if (attachment == null)
            {
                sink.Complete();
                await sink.DisposeAsync();
                return CommandObservationBindingResult<NyxIdChatStartError>.Failure(NyxIdChatStartError.ProjectionUnavailable);
            }

            target.BindLiveObservation(attachment.ProjectionLease, attachment.LiveSinkLease, sink, turnId);
            return CommandObservationBindingResult<NyxIdChatStartError>.Success();
        }
        catch
        {
            sink.Complete();
            await sink.DisposeAsync();
            throw;
        }
    }
}

internal sealed class NyxIdChatCommandEnvelopeFactory : ICommandEnvelopeFactory<NyxIdChatCommand>
{
    public EventEnvelope CreateEnvelope(NyxIdChatCommand command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        var taskId = CreateTaskId(command.ActorId, command.TurnId);
        var startTurn = new NyxIdChatStartTurnCommand
        {
            Prompt = command.Prompt,
            ScopeId = command.ScopeId,
            ConversationActorId = command.ActorId,
            TurnId = command.TurnId,
            TaskId = taskId,
            PlanId = CreatePlanId(command.ActorId, command.TurnId),
            PlanRevision = 1,
            AddedBy = NyxIdChatStepAddedBy.Initial,
            ClientRequestId = command.ClientRequestId?.Trim() ?? string.Empty,
            CommandId = context.CommandId,
            CorrelationId = context.CorrelationId,
            InputPartsFingerprint = command.InputPartsFingerprint?.Trim() ?? string.Empty,
        };
        if (command.InputParts is { Count: > 0 })
        {
            foreach (var part in command.InputParts)
                startTurn.InputParts.Add(part.Clone());
        }

        var control = command.LlmControl ?? LLMControlContext.Empty;
        var effectiveControl = control with
        {
            NyxIdAccessToken = string.IsNullOrWhiteSpace(command.AccessToken)
                ? control.NyxIdAccessToken
                : command.AccessToken.Trim(),
        };
        startTurn.LlmControl = effectiveControl.ToPayload();
        startTurn.ToolContext = BuildToolContext(command, effectiveControl).ToPayload();
        startTurn.ContextAttachments = ConversationContextAttachmentAdmission.CloneOptionalSet(command.ContextAttachments);
        AppendExternalContext(startTurn.ToolContext.ExternalMetadata, command.Metadata);

        if (!command.CreateIfMissing)
            return CreateDirectEnvelope(context, startTurn);

        return CreateDirectEnvelope(context, new NyxIdChatConversationCreateCommand
        {
            ScopeId = command.ScopeId,
            CreatedLocally = command.CreatedLocally,
            AgentProfile = command.AgentProfile?.Clone(),
            AgentProfileReference = command.AgentProfileReference?.Clone(),
            ContextAttachments = ConversationContextAttachmentAdmission.CloneOptionalSet(command.ContextAttachments),
            RequestedActorId = command.ActorId,
            FirstTurn = startTurn,
        });
    }

    private static AgentToolExecutionContext BuildToolContext(NyxIdChatCommand command, LLMControlContext effectiveControl)
    {
        var skillRecovery = SkillInvocationTriggerParser.TryParse(command.Prompt, platform: "cli", out var trigger)
            ? AgentSkillRecoveryContextBuilder.FromTrigger(trigger)
            : AgentSkillRecoveryContext.Empty;
        var toolContext = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(command.TurnId, null),
            Credentials = new AgentToolCredentials(
                command.AccessToken,
                null,
                null,
                command.NyxIdCredentialKind),
            Caller = new AgentToolCallerContext(
                command.ScopeId,
                command.OwnerSubject,
                command.TurnId,
                command.ScopeId),
            Channel = new AgentToolChannelContext("nyxid-chat", null, command.ScopeId, null, null),
            SkillRecovery = skillRecovery,
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "nyxid",
                string.Empty,
                command.OwnerSubject,
                "proxy"),
            InvocationSurface = AgentToolInvocationSurface.HumanSession,
            Chat = new AgentChatInvocationContext(
                AgentChatInvocationSurface.NyxIdAssistant,
                command.ActorId.Trim(),
                command.TurnId.Trim(),
                CreateTaskId(command.ActorId, command.TurnId),
                null,
                null),
        };
        return effectiveControl.ToToolContext(toolContext);
    }

    private static void AppendExternalContext(
        Google.Protobuf.Collections.MapField<string, string> destination,
        IReadOnlyDictionary<string, string>? source)
    {
        if (source == null)
            return;

        foreach (var (key, value) in source)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                destination[key.Trim()] = value.Trim();
        }
    }

    private static string CreateTaskId(string actorId, string turnId)
    {
        var normalizedActorId = actorId?.Trim() ?? string.Empty;
        var normalizedTurnId = turnId?.Trim() ?? string.Empty;
        var identity = $"{normalizedActorId.Length}:{normalizedActorId}{normalizedTurnId.Length}:{normalizedTurnId}";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity));
        return $"task-{Convert.ToHexStringLower(hash)[..32]}";
    }

    private static string CreatePlanId(string actorId, string turnId)
    {
        var normalizedActorId = actorId?.Trim() ?? string.Empty;
        var normalizedTurnId = turnId?.Trim() ?? string.Empty;
        var identity = $"{normalizedActorId.Length}:{normalizedActorId}{normalizedTurnId.Length}:{normalizedTurnId}";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity));
        return $"plan-{Convert.ToHexStringLower(hash)[..32]}";
    }

    private static EventEnvelope CreateDirectEnvelope(CommandContext context, IMessage message) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(message),
            Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = context.TargetId } },
            Propagation = new EnvelopePropagation { CorrelationId = context.CorrelationId },
        };
}

internal sealed class NyxIdApprovalCommandEnvelopeFactory : ICommandEnvelopeFactory<NyxIdApprovalCommand>
{
    public EventEnvelope CreateEnvelope(NyxIdApprovalCommand command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new ToolApprovalDecisionEvent
            {
                RequestId = command.RequestId,
                ContinuationTurnId = command.TurnId,
                Approved = command.Approved,
                Reason = command.Reason,
            }),
            Route = new EnvelopeRoute { Direct = new DirectRoute { TargetActorId = context.TargetId } },
            Propagation = new EnvelopePropagation { CorrelationId = context.CorrelationId },
        };
    }
}

internal sealed class NyxIdActionContinuationCommandEnvelopeFactory
    : ICommandEnvelopeFactory<NyxIdActionContinuationCommand>
{
    public EventEnvelope CreateEnvelope(
        NyxIdActionContinuationCommand command,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        var message = new NyxIdChatActionContinueCommand
        {
            ScopeId = command.ScopeId,
            ConversationActorId = command.ActorId,
            OriginTurnId = command.OriginTurnId,
            ContinuationTurnId = command.ContinuationTurnId,
            OwnerSubject = command.OwnerSubject,
            ClientRequestId = command.ClientRequestId,
            CommandId = context.CommandId,
            CorrelationId = context.CorrelationId,
        };
        if (command.ToolContext is not null)
            message.ToolContext = command.ToolContext.Clone();
        message.Actions.Add(command.Actions.Select(static action => action.Clone()));

        return new EventEnvelope
        {
            Id = context.CommandId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(message),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = context.TargetId },
            },
            Propagation = new EnvelopePropagation
            {
                CorrelationId = context.CorrelationId,
            },
        };
    }
}

internal sealed class NyxIdChatAcceptedReceiptFactory
    : ICommandReceiptFactory<NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt>
{
    public NyxIdChatAcceptedReceipt Create(
        NyxIdChatCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new NyxIdChatAcceptedReceipt(
            target.ActorId,
            context.CommandId,
            context.CorrelationId,
            target.TurnId,
            target.ScopeId);
    }
}

internal sealed class NyxIdChatCompletionPolicy
    : ICommandCompletionPolicy<AGUIEvent, NyxIdChatCompletionStatus>
{
    public NyxIdChatCompletionStatus IncompleteCompletion => NyxIdChatCompletionStatus.Unknown;

    public bool TryResolve(AGUIEvent evt, out NyxIdChatCompletionStatus completion)
    {
        ArgumentNullException.ThrowIfNull(evt);

        completion = evt.EventCase switch
        {
            AGUIEvent.EventOneofCase.RunFinished => NyxIdChatCompletionStatus.Completed,
            AGUIEvent.EventOneofCase.RunError => NyxIdChatCompletionStatus.Failed,
            _ => NyxIdChatCompletionStatus.Unknown,
        };
        return completion != NyxIdChatCompletionStatus.Unknown;
    }
}

internal sealed class NyxIdChatFinalizeEmitter
    : ICommandFinalizeEmitter<NyxIdChatAcceptedReceipt, NyxIdChatCompletionStatus, AGUIEvent>
{
    public Task EmitAsync(
        NyxIdChatAcceptedReceipt receipt,
        NyxIdChatCompletionStatus completion,
        bool completed,
        Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        // Refactor (iter21/cluster-002-request-path-projection-session-priming):
        //   Old pattern: request handlers synchronously ensure projection/session leases and wait on live sinks.
        //   New principle: commands use accepted receipts; observation is owned by binders or attach-only sessions.
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(emitAsync);

        if (completion.DurableTerminal is not null)
            return emitAsync(completion.DurableTerminal, ct).AsTask();

        if (completed)
            return Task.CompletedTask;

        return emitAsync(
            new AGUIEvent
            {
                RunError = new RunErrorEvent
                {
                    Message = "Request timed out.",
                    Code = "timeout",
                },
            },
            ct).AsTask();
    }
}

internal sealed class NyxIdChatDurableCompletionResolver
    : ICommandDurableCompletionResolver<NyxIdChatAcceptedReceipt, NyxIdChatCompletionStatus>
{
    private readonly INyxIdChatConversationStateQueryPort? _stateQueryPort;

    public NyxIdChatDurableCompletionResolver(
        INyxIdChatConversationStateQueryPort? stateQueryPort = null)
    {
        _stateQueryPort = stateQueryPort;
    }

    public async Task<CommandDurableCompletionObservation<NyxIdChatCompletionStatus>> ResolveAsync(
        NyxIdChatAcceptedReceipt receipt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (_stateQueryPort is null || string.IsNullOrWhiteSpace(receipt.ScopeId))
            return CommandDurableCompletionObservation<NyxIdChatCompletionStatus>.Incomplete;

        NyxIdChatConversationStateQueryResult result;
        try
        {
            result = await _stateQueryPort.GetAsync(
                new NyxIdChatConversationStateQuery(
                    receipt.ScopeId,
                    receipt.ActorId,
                    TurnId: receipt.TurnId),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return CommandDurableCompletionObservation<NyxIdChatCompletionStatus>.Incomplete;
        }

        var state = result.Snapshot;
        if (result.Status != NyxIdChatConversationStateQueryStatus.Current ||
            state is null ||
            !string.Equals(state.ActorId, receipt.ActorId, StringComparison.Ordinal) ||
            !string.Equals(state.ScopeId, receipt.ScopeId, StringComparison.Ordinal))
        {
            return CommandDurableCompletionObservation<NyxIdChatCompletionStatus>.Incomplete;
        }

        var turn = ResolveTurn(state, receipt.TurnId);
        var task = state.ActiveTask;
        if (turn is null || task is null ||
            !MatchesRequestIdentity(state.ContinuationAdmission, turn, receipt) ||
            !string.Equals(task.TurnId, receipt.TurnId, StringComparison.Ordinal) ||
            !string.Equals(task.TaskId, turn.TaskId, StringComparison.Ordinal))
        {
            return CommandDurableCompletionObservation<NyxIdChatCompletionStatus>.Incomplete;
        }

        var terminal = BuildTerminal(receipt, state.ProgressSequence, task, turn);
        return terminal is null
            ? CommandDurableCompletionObservation<NyxIdChatCompletionStatus>.Incomplete
            : new CommandDurableCompletionObservation<NyxIdChatCompletionStatus>(
                true,
                (terminal.EventCase == AGUIEvent.EventOneofCase.RunError
                    ? NyxIdChatCompletionStatus.Failed
                    : NyxIdChatCompletionStatus.Completed).WithDurableTerminal(terminal));
    }

    private static bool MatchesRequestIdentity(
        NyxIdChatContinuationAdmissionSnapshot? admission,
        NyxIdChatConversationTurnSnapshot turn,
        NyxIdChatAcceptedReceipt receipt) =>
        string.Equals(turn.CommandId, receipt.CommandId, StringComparison.Ordinal) ||
        (admission is not null &&
         string.Equals(admission.Kind, "action", StringComparison.Ordinal) &&
         string.Equals(admission.RequestId, receipt.CommandId, StringComparison.Ordinal) &&
         string.Equals(admission.ContinuationTurnId, receipt.TurnId, StringComparison.Ordinal));

    private static NyxIdChatConversationTurnSnapshot? ResolveTurn(
        NyxIdChatConversationStateSnapshot state,
        string turnId) =>
        new[] { state.ActiveTurn, state.LatestTurn }
            .Concat(state.RecentTerminalTurns.Cast<NyxIdChatConversationTurnSnapshot?>())
            .FirstOrDefault(turn => string.Equals(turn?.TurnId, turnId, StringComparison.Ordinal));

    private static AGUIEvent? BuildTerminal(
        NyxIdChatAcceptedReceipt receipt,
        long sequence,
        NyxIdChatConversationTaskSnapshot task,
        NyxIdChatConversationTurnSnapshot turn)
    {
        if (!System.Enum.TryParse<NyxIdChatTaskStatus>(task.Status, true, out var taskStatus) ||
            !System.Enum.TryParse<NyxIdChatTurnStatus>(turn.Status, true, out var turnStatus))
        {
            return null;
        }

        if (taskStatus == NyxIdChatTaskStatus.Active &&
            turnStatus == NyxIdChatTurnStatus.Active)
        {
            return HasRecoverableTerminalStep(task)
                ? new AGUIEvent
                {
                    Sequence = sequence,
                    RunFinished = new RunFinishedEvent
                    {
                        ThreadId = receipt.ActorId,
                        RunId = receipt.TurnId,
                        Status = RunCompletionStatus.Blocked,
                        Result = Any.Pack(new StringValue()),
                    },
                }
                : null;
        }

        return NyxIdChatConversationAguiFrameBuilder.BuildTerminal(
            receipt.ActorId,
            receipt.TurnId,
            new NyxIdChatConversationGAgentState
            {
                ActiveTask = new NyxIdChatTaskState
                {
                    TaskId = task.TaskId,
                    TurnId = task.TurnId,
                    Status = taskStatus,
                    FailureCode = task.FailureCode ?? string.Empty,
                    SafeMessage = task.SafeMessage ?? string.Empty,
                },
                ActiveTurn = new NyxIdChatTurnState
                {
                    TaskId = turn.TaskId,
                    TurnId = turn.TurnId,
                    Status = turnStatus,
                    FailureCode = turn.FailureCode ?? string.Empty,
                    SafeMessage = turn.SafeMessage ?? string.Empty,
                },
            },
            sequence);
    }

    private static bool HasRecoverableTerminalStep(NyxIdChatConversationTaskSnapshot task)
    {
        var step = task.Steps.FirstOrDefault(candidate =>
            string.Equals(candidate.StepId, task.ActiveStepId, StringComparison.Ordinal));
        return step is not null &&
               System.Enum.TryParse<NyxIdChatStepStatus>(step.Status, true, out var status) &&
               NyxIdChatConversationAguiFrameBuilder.IsRecoverableTerminalStep(
                   status,
                   step.AvailableActions?.Retry == true,
                   step.AvailableActions?.Skip == true);
    }
}

internal static class NyxIdChatInteractionFactories
{
    public static ICommandTargetResolver<NyxIdChatCommand, NyxIdChatCommandTarget, NyxIdChatStartError> CreateChatResolver(
        IServiceProvider sp) =>
        new NyxIdChatCommandTargetResolver(
            sp.GetRequiredService<IActorRuntime>(),
            sp.GetRequiredService<INyxIdChatSessionProjectionPort>(),
            () => sp.GetRequiredService<ICommandTargetResolver<
                NyxIdChatConversationCreateCommand,
                NyxIdChatConversationCreateCommandTarget,
                NyxIdChatLifecycleCommandStartError>>());

    public static ICommandTargetResolver<NyxIdApprovalCommand, NyxIdChatCommandTarget, NyxIdChatStartError> CreateApprovalResolver(
        IServiceProvider sp) =>
        new NyxIdChatCommandTargetResolver<NyxIdApprovalCommand>(
            sp.GetRequiredService<IActorRuntime>(),
            sp.GetRequiredService<INyxIdChatSessionProjectionPort>(),
            static command => command.ActorId);

    public static ICommandTargetResolver<NyxIdActionContinuationCommand, NyxIdChatCommandTarget, NyxIdChatStartError> CreateActionContinuationResolver(
        IServiceProvider sp) =>
        new NyxIdChatCommandTargetResolver<NyxIdActionContinuationCommand>(
            sp.GetRequiredService<IActorRuntime>(),
            sp.GetRequiredService<INyxIdChatSessionProjectionPort>(),
            static command => command.ActorId);

    public static ICommandObservationLifecycle<NyxIdChatCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError> CreateChatObservationLifecycle(
        IServiceProvider sp) =>
        new NyxIdChatObservationLifecycle<NyxIdChatCommand>(
            sp.GetRequiredService<INyxIdChatSessionProjectionPort>(),
            static command => command.TurnId);

    public static ICommandObservationLifecycle<NyxIdApprovalCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError> CreateApprovalObservationLifecycle(
        IServiceProvider sp) =>
        new NyxIdChatObservationLifecycle<NyxIdApprovalCommand>(
            sp.GetRequiredService<INyxIdChatSessionProjectionPort>(),
            static command => command.TurnId);

    public static ICommandObservationLifecycle<NyxIdActionContinuationCommand, NyxIdChatCommandTarget, NyxIdChatAcceptedReceipt, NyxIdChatStartError> CreateActionContinuationObservationLifecycle(
        IServiceProvider sp) =>
        new NyxIdChatObservationLifecycle<NyxIdActionContinuationCommand>(
            sp.GetRequiredService<INyxIdChatSessionProjectionPort>(),
            static command => command.ContinuationTurnId);
}
