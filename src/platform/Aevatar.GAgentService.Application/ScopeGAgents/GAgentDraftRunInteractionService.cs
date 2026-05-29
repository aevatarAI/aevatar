using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Presentation.AGUI;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Application.ScopeGAgents;

// Refactor (iter56/cluster-868-endpoint-runtime-lifecycle): old=endpoint direct IActorRuntime, new=IGAgentDraftRunInteractionPort + CQRS Core
// Host now passes normalized DraftRun intent and SSE callbacks only; Application owns actor provisioning and rollback.
// Rollback wraps ICommandInteractionService.ExecuteAsync so pre-dispatch observation failures are compensated without CQRS Core semantic changes.
// The port remains DraftRun-specific and does not expose runtime, projection leases, or generic command targets.
internal sealed class GAgentDraftRunInteractionService : IGAgentDraftRunInteractionPort
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IGAgentActorRegistryCommandPort _registryCommandPort;
    private readonly IScopeResourceAdmissionPort _admissionPort;
    private readonly ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus> _interactionService;
    private readonly ILogger<GAgentDraftRunInteractionService>? _logger;

    public GAgentDraftRunInteractionService(
        IActorRuntime actorRuntime,
        IGAgentActorRegistryCommandPort registryCommandPort,
        IScopeResourceAdmissionPort admissionPort,
        ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus> interactionService,
        ILogger<GAgentDraftRunInteractionService>? logger = null)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _registryCommandPort = registryCommandPort ?? throw new ArgumentNullException(nameof(registryCommandPort));
        _admissionPort = admissionPort ?? throw new ArgumentNullException(nameof(admissionPort));
        _interactionService = interactionService ?? throw new ArgumentNullException(nameof(interactionService));
        _logger = logger;
    }

    public async Task<CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>> ExecuteAsync(
        GAgentDraftRunInteractionRequest request,
        Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
        Func<GAgentDraftRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(emitAsync);

        var preparedActor = await PrepareAsync(request, ct);
        if (preparedActor.Error != GAgentDraftRunStartError.None)
            return CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>.Failure(preparedActor.Error);

        var actor = preparedActor.Actor!;
        try
        {
            var command = new GAgentDraftRunCommand(
                ScopeId: request.ScopeId.Trim(),
                ActorTypeName: actor.ActorTypeName,
                Prompt: request.Prompt.Trim(),
                PreferredActorId: actor.ActorId,
                SessionId: string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId.Trim(),
                NyxIdAccessToken: NormalizeOptional(request.NyxIdAccessToken),
                ModelOverride: NormalizeOptional(request.ModelOverride),
                PreferredLlmRoute: NormalizeOptional(request.PreferredLlmRoute),
                InputParts: request.InputParts,
                ToolContext: request.ToolContext,
                LlmControl: request.LlmControl);

            var result = await _interactionService.ExecuteAsync(command, emitAsync, onAcceptedAsync, ct);
            if (!result.Succeeded)
                await RollbackAsync(actor, CancellationToken.None);

            return result;
        }
        catch
        {
            await RollbackAsync(actor, CancellationToken.None);
            throw;
        }
    }

    private async Task<PreparationResult> PrepareAsync(
        GAgentDraftRunInteractionRequest request,
        CancellationToken ct)
    {
        var scopeId = request.ScopeId.Trim();
        var actorTypeName = request.ActorTypeName.Trim();
        var actorType = ScopeGAgentActorTypeResolver.Resolve(actorTypeName);
        if (actorType is null)
            return PreparationResult.Failure(GAgentDraftRunStartError.UnknownActorType);

        var actorId = string.IsNullOrWhiteSpace(request.PreferredActorId)
            ? AgentId.New(actorType)
            : request.PreferredActorId.Trim();
        var existingActor = await _actorRuntime.GetAsync(actorId);
        if (existingActor is not null)
        {
            var admission = await _admissionPort.AuthorizeTargetAsync(
                new ScopeResourceTarget(
                    scopeId,
                    ScopeResourceKind.GAgentActor,
                    actorTypeName,
                    actorId,
                    ScopeResourceOperation.DraftRunReuse),
                ct);
            if (!admission.IsAllowed)
                return PreparationResult.Failure(GAgentDraftRunStartError.ActorTypeMismatch);

            return PreparationResult.Success(new GAgentDraftRunPreparedActor(
                scopeId,
                actorTypeName,
                actorId,
                RequiresRollbackOnFailure: false));
        }

        IActor? createdActor = null;
        try
        {
            createdActor = await _actorRuntime.CreateAsync(actorType, actorId, ct);
            var receipt = await _registryCommandPort.RegisterActorAsync(
                new GAgentActorRegistration(scopeId, actorTypeName, actorId),
                ct);
            if (!receipt.IsAdmissionVisible)
            {
                await RollbackAsync(
                    new GAgentDraftRunPreparedActor(scopeId, actorTypeName, actorId, RequiresRollbackOnFailure: true),
                    CancellationToken.None);
                return PreparationResult.Failure(GAgentDraftRunStartError.ActorTypeMismatch);
            }
        }
        catch
        {
            if (createdActor is not null)
            {
                await RollbackAsync(
                    new GAgentDraftRunPreparedActor(scopeId, actorTypeName, actorId, RequiresRollbackOnFailure: true),
                    CancellationToken.None);
            }

            throw;
        }

        return PreparationResult.Success(new GAgentDraftRunPreparedActor(
            scopeId,
            actorTypeName,
            actorId,
            RequiresRollbackOnFailure: true));
    }

    private async Task RollbackAsync(
        GAgentDraftRunPreparedActor preparedActor,
        CancellationToken ct)
    {
        if (!preparedActor.RequiresRollbackOnFailure)
            return;

        if (!await TryUnregisterDraftRunActorAsync(
                preparedActor.ScopeId,
                preparedActor.ActorTypeName,
                preparedActor.ActorId,
                ct))
            return;

        try
        {
            await _actorRuntime.DestroyAsync(preparedActor.ActorId, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to destroy draft-run actor {ActorId} during rollback", preparedActor.ActorId);
        }
    }

    private async Task<bool> TryUnregisterDraftRunActorAsync(
        string scopeId,
        string actorTypeName,
        string actorId,
        CancellationToken ct)
    {
        try
        {
            await _registryCommandPort.UnregisterActorAsync(
                new GAgentActorRegistration(scopeId, actorTypeName, actorId),
                ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to remove draft-run actor {ActorId} from registry during rollback", actorId);
            return false;
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private sealed record PreparationResult(
        GAgentDraftRunPreparedActor? Actor,
        GAgentDraftRunStartError Error)
    {
        public static PreparationResult Success(GAgentDraftRunPreparedActor actor)
        {
            ArgumentNullException.ThrowIfNull(actor);
            return new PreparationResult(actor, GAgentDraftRunStartError.None);
        }

        public static PreparationResult Failure(GAgentDraftRunStartError error) => new(null, error);
    }
}
