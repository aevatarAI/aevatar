using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Voice;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat.Voice;

public interface INyxIdVoiceAgentCommandService
{
    Task<NyxIdVoiceAgentProvisionResult> ProvisionAsync(
        NyxIdVoiceAgentProvisionCommand command,
        CancellationToken ct = default);

    Task<NyxIdVoiceAgentDeleteResult> DeleteAsync(
        NyxIdVoiceAgentDeleteCommand command,
        CancellationToken ct = default);
}

public sealed record NyxIdVoiceAgentProvisionCommand(string ScopeId, string ModuleName);

public sealed record NyxIdVoiceAgentProvisionResult(
    NyxIdVoiceAgentProvisionStatus Status,
    string ActorId,
    string ModuleName,
    string CommandId = "",
    string CorrelationId = "",
    string Stage = "");

public enum NyxIdVoiceAgentProvisionStatus
{
    Accepted = 0,
    AdmissionUnavailable = 1,
    Failed = 2,
}

public sealed record NyxIdVoiceAgentDeleteCommand(string ScopeId, string ActorId);

public sealed record NyxIdVoiceAgentDeleteResult(NyxIdVoiceAgentDeleteStatus Status);

public enum NyxIdVoiceAgentDeleteStatus
{
    Deleted = 0,
    NotFound = 1,
    Denied = 2,
    AdmissionUnavailable = 3,
    Failed = 4,
}

public sealed class NyxIdVoiceAgentCommandService : INyxIdVoiceAgentCommandService
{
    private readonly IActorRuntime _actorRuntime;
    private readonly IGAgentActorRegistryCommandPort _registryCommandPort;
    private readonly IScopeResourceAdmissionPort _admissionPort;
    private readonly IVoicePresenceCapabilityCommandPort _voiceCommandPort;
    private readonly ILogger<NyxIdVoiceAgentCommandService> _logger;

    public NyxIdVoiceAgentCommandService(
        IActorRuntime actorRuntime,
        IGAgentActorRegistryCommandPort registryCommandPort,
        IScopeResourceAdmissionPort admissionPort,
        IVoicePresenceCapabilityCommandPort voiceCommandPort,
        ILogger<NyxIdVoiceAgentCommandService> logger)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _registryCommandPort = registryCommandPort ?? throw new ArgumentNullException(nameof(registryCommandPort));
        _admissionPort = admissionPort ?? throw new ArgumentNullException(nameof(admissionPort));
        _voiceCommandPort = voiceCommandPort ?? throw new ArgumentNullException(nameof(voiceCommandPort));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NyxIdVoiceAgentProvisionResult> ProvisionAsync(
        NyxIdVoiceAgentProvisionCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var scopeId = NormalizeRequired(command.ScopeId, nameof(command.ScopeId));
        var moduleName = NormalizeRequired(command.ModuleName, nameof(command.ModuleName));
        var actorId = NyxIdVoiceServiceDefaults.GenerateActorId();
        var actorCreated = false;
        var registrationAttempted = false;

        try
        {
            await _actorRuntime.CreateAsync<NyxIdVoiceGAgent>(actorId, ct);
            actorCreated = true;

            var registration = new GAgentActorRegistration(
                scopeId,
                NyxIdVoiceServiceDefaults.GAgentKind,
                actorId);
            registrationAttempted = true;
            var registryReceipt = await _registryCommandPort.RegisterActorAsync(registration, ct);
            if (!registryReceipt.IsAdmissionVisible)
            {
                await TryRollbackAsync(scopeId, actorId, registrationAttempted, CancellationToken.None);
                return new NyxIdVoiceAgentProvisionResult(
                    NyxIdVoiceAgentProvisionStatus.AdmissionUnavailable,
                    actorId,
                    moduleName);
            }

            var voiceReceipt = await _voiceCommandPort.EnableAsync(
                actorId,
                new VoicePresenceEnableRequested
                {
                    ModuleName = moduleName,
                    RemoteAudioSupport = VoiceRemoteAudioSupport.Supported,
                    VoiceSessionDefaults = new VoiceSessionDefaults(),
                },
                ct);

            return new NyxIdVoiceAgentProvisionResult(
                NyxIdVoiceAgentProvisionStatus.Accepted,
                actorId,
                voiceReceipt.ModuleName,
                voiceReceipt.CommandId,
                voiceReceipt.CorrelationId,
                voiceReceipt.Stage);
        }
        catch (OperationCanceledException)
        {
            if (actorCreated)
                await TryRollbackAsync(scopeId, actorId, registrationAttempted, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision NyxID voice agent {ActorId}", actorId);
            if (actorCreated)
                await TryRollbackAsync(scopeId, actorId, registrationAttempted, CancellationToken.None);
            return new NyxIdVoiceAgentProvisionResult(
                NyxIdVoiceAgentProvisionStatus.Failed,
                actorId,
                moduleName);
        }
    }

    public async Task<NyxIdVoiceAgentDeleteResult> DeleteAsync(
        NyxIdVoiceAgentDeleteCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var scopeId = NormalizeRequired(command.ScopeId, nameof(command.ScopeId));
        var actorId = NormalizeRequired(command.ActorId, nameof(command.ActorId));

        try
        {
            var admission = await _admissionPort.AuthorizeTargetAsync(
                new ScopeResourceTarget(
                    scopeId,
                    ScopeResourceKind.GAgentActor,
                    NyxIdVoiceServiceDefaults.GAgentKind,
                    actorId,
                    ScopeResourceOperation.Delete),
                ct);
            var admissionStatus = admission.Status switch
            {
                ScopeResourceAdmissionStatus.Allowed => (NyxIdVoiceAgentDeleteStatus?)null,
                ScopeResourceAdmissionStatus.NotFound => NyxIdVoiceAgentDeleteStatus.NotFound,
                ScopeResourceAdmissionStatus.Denied or ScopeResourceAdmissionStatus.ScopeMismatch =>
                    NyxIdVoiceAgentDeleteStatus.Denied,
                _ => NyxIdVoiceAgentDeleteStatus.AdmissionUnavailable,
            };
            if (admissionStatus.HasValue)
                return new NyxIdVoiceAgentDeleteResult(admissionStatus.Value);

            var registration = new GAgentActorRegistration(
                scopeId,
                NyxIdVoiceServiceDefaults.GAgentKind,
                actorId);
            var registryReceipt = await _registryCommandPort.UnregisterActorAsync(registration, ct);
            if (registryReceipt.Stage != GAgentActorRegistryCommandStage.AdmissionRemoved)
            {
                return new NyxIdVoiceAgentDeleteResult(
                    NyxIdVoiceAgentDeleteStatus.AdmissionUnavailable);
            }

            await _actorRuntime.DestroyAsync(actorId, ct);
            return new NyxIdVoiceAgentDeleteResult(NyxIdVoiceAgentDeleteStatus.Deleted);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete NyxID voice agent {ActorId}", actorId);
            return new NyxIdVoiceAgentDeleteResult(NyxIdVoiceAgentDeleteStatus.Failed);
        }
    }

    private async Task TryRollbackAsync(
        string scopeId,
        string actorId,
        bool registrationAttempted,
        CancellationToken ct)
    {
        if (registrationAttempted)
        {
            try
            {
                await _registryCommandPort.UnregisterActorAsync(
                    new GAgentActorRegistration(
                        scopeId,
                        NyxIdVoiceServiceDefaults.GAgentKind,
                        actorId),
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to unregister NyxID voice agent {ActorId} during rollback",
                    actorId);
                return;
            }
        }

        try
        {
            await _actorRuntime.DestroyAsync(actorId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to destroy NyxID voice agent {ActorId} during rollback", actorId);
        }
    }

    private static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException($"{paramName} is required.", paramName);
        return normalized;
    }
}
