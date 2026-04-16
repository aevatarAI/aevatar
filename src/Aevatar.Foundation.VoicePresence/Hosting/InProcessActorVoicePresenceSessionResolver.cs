using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Modules;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.VoicePresence.Hosting;

/// <summary>
/// Resolves voice sessions from in-process actor activations that expose dynamic event modules.
/// </summary>
public sealed class InProcessActorVoicePresenceSessionResolver : IVoicePresenceSessionResolver
{
    private const string DefaultVoiceModuleName = "voice_presence";
    private readonly IServiceProvider _services;

    public InProcessActorVoicePresenceSessionResolver(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public async Task<VoicePresenceSession?> ResolveAsync(VoicePresenceSessionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actorId = request.ActorId;
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        var actorRuntime = _services.GetService<IActorRuntime>();
        if (actorRuntime == null)
            return null;

        var actor = await actorRuntime.GetAsync(actorId);
        if (actor?.Agent is not IEventModuleContainer<IEventHandlerContext> moduleContainer)
            return null;

        var module = ResolveVoiceModule(moduleContainer.GetModules(), request.ModuleName);
        if (module == null)
            return null;

        return new VoicePresenceSession(
            module,
            (message, dispatchCt) => DispatchSelfEventAsync(actorId, module.Name, message, dispatchCt),
            module.PcmSampleRateHz);
    }

    private Task DispatchSelfEventAsync(
        string actorId,
        string moduleName,
        IMessage message,
        CancellationToken ct)
    {
        var dispatchPort = _services.GetService<IActorDispatchPort>()
            ?? throw new InvalidOperationException(
                $"{nameof(IActorDispatchPort)} is required to dispatch voice self events.");

        return dispatchPort.DispatchAsync(
            actorId,
            VoicePresenceSessionDispatch.BuildSelfEnvelope(actorId, moduleName, message),
            ct);
    }

    private static VoicePresenceModule? ResolveVoiceModule(
        IReadOnlyList<IEventModule<IEventHandlerContext>> modules,
        string? requestedModuleName)
    {
        var voiceModules = modules.OfType<VoicePresenceModule>().ToList();
        if (voiceModules.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(requestedModuleName))
        {
            var requestedMatches = voiceModules
                .Where(module => string.Equals(module.Name, requestedModuleName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return requestedMatches.Count == 1
                ? requestedMatches[0]
                : null;
        }

        if (voiceModules.Count == 1)
            return voiceModules[0];

        var defaultMatches = voiceModules
            .Where(static module => string.Equals(module.Name, DefaultVoiceModuleName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return defaultMatches.Count == 1
            ? defaultMatches[0]
            : null;
    }
}
