using System.Runtime.ExceptionServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Presentation.AGUI;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.ScopeGAgents;

internal sealed class GAgentDraftRunCommandTarget
    : IActorCommandDispatchTarget,
      ICommandEventTarget<AGUIEvent>,
      ICommandInteractionCleanupTarget<GAgentDraftRunAcceptedReceipt, GAgentDraftRunCompletionStatus>,
      ICommandDispatchCleanupAware
{
    private readonly IGAgentDraftRunProjectionPort _projectionPort;

    public GAgentDraftRunCommandTarget(
        IActor actor,
        string actorTypeName,
        IGAgentDraftRunProjectionPort projectionPort)
    {
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        ActorTypeName = string.IsNullOrWhiteSpace(actorTypeName)
            ? throw new ArgumentException("Actor type name is required.", nameof(actorTypeName))
            : actorTypeName.Trim();
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public IActor Actor { get; }
    public string ActorTypeName { get; }
    public string TargetId => Actor.Id;
    public string ActorId => Actor.Id;
    public IGAgentDraftRunProjectionLease? ProjectionLease { get; private set; }
    public IEventSink<AGUIEvent>? LiveSink { get; private set; }

    public void BindLiveObservation(
        IGAgentDraftRunProjectionLease lease,
        IEventSink<AGUIEvent> sink)
    {
        ProjectionLease = lease ?? throw new ArgumentNullException(nameof(lease));
        LiveSink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public IEventSink<AGUIEvent> RequireLiveSink() =>
        LiveSink ?? throw new InvalidOperationException("GAgent draft-run live sink is not bound.");

    public Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default) =>
        ReleaseAsync(ct);

    public Task ReleaseAfterInteractionAsync(
        GAgentDraftRunAcceptedReceipt receipt,
        CommandInteractionCleanupContext<GAgentDraftRunCompletionStatus> cleanup,
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
                    sink,
                    null,
                    ct);
                ProjectionLease = null;
                LiveSink = null;
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }
        else
        {
            if (sink != null)
            {
                try
                {
                    sink.Complete();
                    await sink.DisposeAsync();
                    LiveSink = null;
                }
                catch (Exception ex)
                {
                    firstException ??= ex;
                }
            }

            if (projectionLease != null)
            {
                try
                {
                    await _projectionPort.ReleaseActorProjectionAsync(projectionLease, ct);
                    ProjectionLease = null;
                }
                catch (Exception ex)
                {
                    firstException ??= ex;
                }
            }
        }

        if (firstException != null)
            ExceptionDispatchInfo.Capture(firstException).Throw();
    }
}

internal sealed class GAgentDraftRunCommandTargetResolver
    : ICommandTargetResolver<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunStartError>
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IGAgentActorStore _actorStore;
    private readonly IGAgentDraftRunProjectionPort _projectionPort;
    private readonly ILogger<GAgentDraftRunCommandTargetResolver> _logger;

    public GAgentDraftRunCommandTargetResolver(
        IActorRuntime actorRuntime,
        IGAgentActorStore actorStore,
        IGAgentDraftRunProjectionPort projectionPort,
        ILogger<GAgentDraftRunCommandTargetResolver> logger)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorStore = actorStore ?? throw new ArgumentNullException(nameof(actorStore));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CommandTargetResolution<GAgentDraftRunCommandTarget, GAgentDraftRunStartError>> ResolveAsync(
        GAgentDraftRunCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var agentType = ScopeGAgentActorTypeResolver.Resolve(command.ActorTypeName);
        if (agentType is null)
        {
            return CommandTargetResolution<GAgentDraftRunCommandTarget, GAgentDraftRunStartError>.Failure(
                GAgentDraftRunStartError.UnknownActorType);
        }

        var preferredActorId = string.IsNullOrWhiteSpace(command.PreferredActorId)
            ? null
            : command.PreferredActorId.Trim();

        IActor actor;
        var isNewActor = false;
        if (preferredActorId is not null)
        {
            var existingActor = await _actorRuntime.GetAsync(preferredActorId);
            if (existingActor != null)
            {
                actor = existingActor;
            }
            else
            {
                actor = await _actorRuntime.CreateAsync(agentType, preferredActorId, ct);
                isNewActor = true;
            }
        }
        else
        {
            actor = await _actorRuntime.CreateAsync(agentType, null, ct);
            isNewActor = true;
        }

        if (isNewActor && command.PersistActorToScopeStore)
        {
            try
            {
                await _actorStore.AddActorAsync(command.ScopeId, command.ActorTypeName.Trim(), actor.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist actor {ActorId} to scope {ScopeId} actor store.", actor.Id, command.ScopeId);
            }
        }

        return CommandTargetResolution<GAgentDraftRunCommandTarget, GAgentDraftRunStartError>.Success(
            new GAgentDraftRunCommandTarget(actor, command.ActorTypeName, _projectionPort));
    }
}

internal sealed class GAgentDraftRunCommandTargetBinder
    : ICommandTargetBinder<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunStartError>
{
    private readonly IGAgentDraftRunProjectionPort _projectionPort;

    public GAgentDraftRunCommandTargetBinder(
        IGAgentDraftRunProjectionPort projectionPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<CommandTargetBindingResult<GAgentDraftRunStartError>> BindAsync(
        GAgentDraftRunCommand command,
        GAgentDraftRunCommandTarget target,
        CommandContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        var sink = new EventChannel<AGUIEvent>();

        try
        {
            var projectionLease = await _projectionPort.EnsureAndAttachAsync(
                token => _projectionPort.EnsureActorProjectionAsync(
                    target.ActorId,
                    context.CommandId,
                    token),
                sink,
                ct);

            if (projectionLease == null)
            {
                sink.Complete();
                await sink.DisposeAsync();
                throw new InvalidOperationException("GAgent draft-run projection pipeline is unavailable.");
            }

            target.BindLiveObservation(projectionLease, sink);
            return CommandTargetBindingResult<GAgentDraftRunStartError>.Success();
        }
        catch
        {
            sink.Complete();
            await sink.DisposeAsync();
            throw;
        }
    }
}

internal sealed class GAgentDraftRunCommandEnvelopeFactory
    : ICommandEnvelopeFactory<GAgentDraftRunCommand>
{
    public EventEnvelope CreateEnvelope(GAgentDraftRunCommand command, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        var sessionId = string.IsNullOrWhiteSpace(command.SessionId)
            ? (command.UseCorrelationIdAsFallbackSessionId ? context.CorrelationId : string.Empty)
            : command.SessionId.Trim();

        var chatRequest = new ChatRequestEvent
        {
            Prompt = command.Prompt,
            SessionId = sessionId,
            ScopeId = command.ScopeId,
        };

        AppendMetadata(chatRequest.Metadata, context.Headers);
        if (!string.IsNullOrWhiteSpace(command.NyxIdAccessToken))
            chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken] = command.NyxIdAccessToken.Trim();
        if (!string.IsNullOrWhiteSpace(command.ModelOverride))
            chatRequest.Metadata[LLMRequestMetadataKeys.ModelOverride] = command.ModelOverride.Trim();
        if (!string.IsNullOrWhiteSpace(command.PreferredLlmRoute))
            chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdRoutePreference] = command.PreferredLlmRoute.Trim();
        if (command.InputParts is { Count: > 0 })
            chatRequest.InputParts.Add(command.InputParts.Select(ToProto));

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(chatRequest),
            Route = EnvelopeRouteSemantics.CreateDirect("api", context.TargetId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = context.CorrelationId,
            },
        };
    }

    private static ChatContentPart ToProto(GAgentDraftRunInputPart source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new ChatContentPart
        {
            Kind = source.Kind switch
            {
                GAgentDraftRunInputPartKind.Text => ChatContentPartKind.Text,
                GAgentDraftRunInputPartKind.Image => ChatContentPartKind.Image,
                GAgentDraftRunInputPartKind.Audio => ChatContentPartKind.Audio,
                GAgentDraftRunInputPartKind.Video => ChatContentPartKind.Video,
                _ => ChatContentPartKind.Unspecified,
            },
            Text = source.Text ?? string.Empty,
            DataBase64 = source.DataBase64 ?? string.Empty,
            MediaType = source.MediaType ?? string.Empty,
            Uri = source.Uri ?? string.Empty,
            Name = source.Name ?? string.Empty,
        };
    }

    private static void AppendMetadata(
        Google.Protobuf.Collections.MapField<string, string> destination,
        IReadOnlyDictionary<string, string>? source)
    {
        if (source == null || source.Count == 0)
            return;

        foreach (var (key, value) in source)
        {
            var normalizedKey = string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim();
            var normalizedValue = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
                continue;

            destination[normalizedKey] = normalizedValue;
        }
    }
}

internal sealed class GAgentDraftRunAcceptedReceiptFactory
    : ICommandReceiptFactory<GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt>
{
    public GAgentDraftRunAcceptedReceipt Create(
        GAgentDraftRunCommandTarget target,
        CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        return new GAgentDraftRunAcceptedReceipt(
            target.ActorId,
            target.ActorTypeName,
            context.CommandId,
            context.CorrelationId);
    }
}

internal sealed class GAgentDraftRunCompletionPolicy
    : ICommandCompletionPolicy<AGUIEvent, GAgentDraftRunCompletionStatus>
{
    public GAgentDraftRunCompletionStatus IncompleteCompletion => GAgentDraftRunCompletionStatus.Unknown;

    public bool TryResolve(
        AGUIEvent evt,
        out GAgentDraftRunCompletionStatus completion)
    {
        ArgumentNullException.ThrowIfNull(evt);

        completion = GAgentDraftRunCompletionStatus.Unknown;
        switch (evt.EventCase)
        {
            case AGUIEvent.EventOneofCase.TextMessageEnd:
                completion = GAgentDraftRunCompletionStatus.TextMessageCompleted;
                return true;
            case AGUIEvent.EventOneofCase.RunFinished:
                completion = GAgentDraftRunCompletionStatus.RunFinished;
                return true;
            case AGUIEvent.EventOneofCase.RunError:
                completion = GAgentDraftRunCompletionStatus.Failed;
                return true;
            default:
                return false;
        }
    }
}

internal sealed class GAgentDraftRunFinalizeEmitter
    : ICommandFinalizeEmitter<GAgentDraftRunAcceptedReceipt, GAgentDraftRunCompletionStatus, AGUIEvent>
{
    public Task EmitAsync(
        GAgentDraftRunAcceptedReceipt receipt,
        GAgentDraftRunCompletionStatus completion,
        bool completed,
        Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(emitAsync);

        if (!completed || completion != GAgentDraftRunCompletionStatus.TextMessageCompleted)
            return Task.CompletedTask;

        return emitAsync(
            new AGUIEvent
            {
                RunFinished = new RunFinishedEvent
                {
                    ThreadId = receipt.ActorId,
                    RunId = receipt.CommandId,
                },
            },
            ct).AsTask();
    }
}

internal sealed class GAgentDraftRunDurableCompletionResolver
    : ICommandDurableCompletionResolver<GAgentDraftRunAcceptedReceipt, GAgentDraftRunCompletionStatus>
{
    public Task<CommandDurableCompletionObservation<GAgentDraftRunCompletionStatus>> ResolveAsync(
        GAgentDraftRunAcceptedReceipt receipt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        _ = ct;
        return Task.FromResult(CommandDurableCompletionObservation<GAgentDraftRunCompletionStatus>.Incomplete);
    }
}
