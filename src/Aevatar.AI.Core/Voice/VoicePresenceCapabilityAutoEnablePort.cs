using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Voice;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Core.Voice;

/// <summary>
/// Provisions a never-enabled default voice agent on first <c>/ws/voice</c> connect by committing a voice
/// capability enable for it — true zero-config. Reuses the SAME enable command the
/// <c>voice-presence/enable</c> endpoint uses (<see cref="IVoicePresenceCapabilityCommandPort.EnableAsync"/>),
/// so the materialized capability is identical to a manual enable.
///
/// The enable command dispatches an envelope directly to the target actor; the admin endpoint guards that
/// dispatch by validating an explicit <c>agentKind</c> query parameter against
/// <see cref="IAgentKindRegistry"/>. On the zero-config attach path there is no caller-supplied agentKind —
/// only the resolved actor id (e.g. the default <c>nyxid-chat-…</c> agent, GAgent kind <c>nyxid.chat</c>).
/// We therefore RESOLVE the kind from the live actor via <see cref="IActorKindProbe"/> (the same runtime
/// kind evidence <c>DefaultAgentKindVerifier</c> reads) and VALIDATE it against the agent-kind registry —
/// mirroring the endpoint's check. Gated on: a non-blank actor id, an actor that actually exists, and a
/// resolvable runtime kind that the registry recognizes; otherwise this is a no-op (so a genuinely-unknown
/// or non-existent actor still fails closed downstream as <c>NotFound</c>).
/// </summary>
public sealed class VoicePresenceCapabilityAutoEnablePort : IVoicePresenceCapabilityAutoEnablePort
{
    // Canonical default module name the capability read path falls back to when no module is specified
    // (mirrors VoicePresenceCapabilityReadModelMapper.DefaultModuleName, which lives in the non-abstractions
    // VoicePresence project this layer does not reference). Enabling under the SAME name the subsequent
    // re-read queries is what makes the bounded re-read observe the new capability.
    private const string DefaultModuleName = "voice_presence";

    private readonly IVoicePresenceCapabilityCommandPort _commandPort;
    private readonly IActorKindProbe? _actorKindProbe;
    private readonly IAgentKindRegistry? _agentKindRegistry;
    private readonly ILogger<VoicePresenceCapabilityAutoEnablePort> _logger;

    // The actor-kind probe and agent-kind registry are optional so the port stays constructible in minimal
    // compositions that wire voice features without the runtime type-system services (e.g. bootstrap
    // coverage). When either is absent the port cannot resolve/validate the default actor's kind, so it
    // fails closed (no auto-provision) — the attach path then surfaces its existing NotFound. In the real
    // host both are registered by the runtime layer, so zero-config provisioning is fully active.
    public VoicePresenceCapabilityAutoEnablePort(
        IVoicePresenceCapabilityCommandPort commandPort,
        IActorKindProbe? actorKindProbe = null,
        IAgentKindRegistry? agentKindRegistry = null,
        ILogger<VoicePresenceCapabilityAutoEnablePort>? logger = null)
    {
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _actorKindProbe = actorKindProbe;
        _agentKindRegistry = agentKindRegistry;
        _logger = logger ?? NullLogger<VoicePresenceCapabilityAutoEnablePort>.Instance;
    }

    public async Task<bool> TryAutoEnableAsync(string actorId, string? moduleName, CancellationToken ct = default)
    {
        var normalizedActorId = actorId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedActorId))
            return false;

        // Without the runtime type-system services there is no way to resolve + validate the default
        // actor's agent kind, so we cannot safely commit an enable to it. Fail closed.
        if (_actorKindProbe is null || _agentKindRegistry is null)
            return false;

        var normalizedModuleName = string.IsNullOrWhiteSpace(moduleName)
            ? DefaultModuleName
            : moduleName.Trim();

        try
        {
            // Resolve the live actor's runtime agent kind. Returns null when the actor does not exist or has
            // no registered kind — both mean "do not auto-provision" (the recovery/self-heal path already ran
            // and produced nothing, and we must never enable a phantom actor). This is the crux of correctly
            // typing the default actor: the kind is read from the actor itself (e.g. "nyxid.chat"), not guessed.
            var runtimeKind = await _actorKindProbe.GetRuntimeAgentKindAsync(normalizedActorId, ct);
            if (string.IsNullOrWhiteSpace(runtimeKind))
            {
                _logger.LogDebug(
                    "voice auto-enable skipped: no runtime agent kind for actor {ActorId} (actor missing or unkinded)",
                    normalizedActorId);
                return false;
            }

            // Validate the resolved kind against the registry — the same guard voice-presence/enable applies
            // to its agentKind parameter. A kind that exists at runtime but is not registered would have its
            // enable rejected, so fail closed here instead of committing a doomed dispatch.
            if (!_agentKindRegistry.TryResolve(runtimeKind, out _))
            {
                _logger.LogDebug(
                    "voice auto-enable skipped: runtime agent kind {AgentKind} for actor {ActorId} is not registered",
                    runtimeKind,
                    normalizedActorId);
                return false;
            }

            // Reuse the exact enable command voice-presence/enable issues: module name = the module the attach
            // will re-read, RemoteAudioSupport = Supported (the /ws/voice transport carries remote audio), and
            // default session config (the command port fills VoiceSessionDefaults when left null). The command
            // port re-checks the actor exists and dispatches the VoicePresenceEnableRequested to the actor.
            var command = new VoicePresenceEnableRequested
            {
                ModuleName = normalizedModuleName,
                RemoteAudioSupport = VoiceRemoteAudioSupport.Supported,
                VoiceSessionDefaults = new VoiceSessionDefaults(),
            };

            var receipt = await _commandPort.EnableAsync(normalizedActorId, command, ct);
            _logger.LogInformation(
                "voice auto-enable: committed enable for never-enabled default agent actor {ActorId} kind {AgentKind} module {ModuleName} (command {CommandId})",
                normalizedActorId,
                runtimeKind,
                receipt.ModuleName,
                receipt.CommandId);
            return true;
        }
        catch (VoicePresenceCapabilityCommandException ex)
        {
            // ActorNotFound / DispatchFailed / InvalidInput: treat as "could not auto-provision" and let the
            // attach path fail closed (NotFound / retryable 503) rather than throw out of the ingress.
            _logger.LogWarning(
                ex,
                "voice auto-enable failed for actor {ActorId} module {ModuleName} ({Error})",
                normalizedActorId,
                normalizedModuleName,
                ex.Error);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "voice auto-enable failed unexpectedly for actor {ActorId} module {ModuleName}",
                normalizedActorId,
                normalizedModuleName);
            return false;
        }
    }
}
