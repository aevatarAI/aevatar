using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.VoicePresence.Modules;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
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

    public async Task<VoicePresenceSession?> ResolveAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        var actorRuntime = _services.GetService<IActorRuntime>();
        if (actorRuntime == null)
            return null;

        var actor = await actorRuntime.GetAsync(actorId);
        if (actor?.Agent is not IEventModuleContainer<IEventHandlerContext> moduleContainer)
            return null;

        var module = ResolveVoiceModule(moduleContainer.GetModules());
        if (module == null)
            return null;

        return new VoicePresenceSession(
            module,
            (message, dispatchCt) => DispatchSelfEventAsync(actorId, message, dispatchCt),
            module.PcmSampleRateHz);
    }

    private Task DispatchSelfEventAsync(
        string actorId,
        IMessage message,
        CancellationToken ct)
    {
        var dispatchPort = _services.GetService<IActorDispatchPort>()
            ?? throw new InvalidOperationException(
                $"{nameof(IActorDispatchPort)} is required to dispatch voice self events.");

        return dispatchPort.DispatchAsync(actorId, BuildSelfEnvelope(actorId, message), ct);
    }

    private static EventEnvelope BuildSelfEnvelope(string actorId, IMessage message) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(message),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(actorId, TopologyAudience.Self),
        };

    private static VoicePresenceModule? ResolveVoiceModule(
        IReadOnlyList<IEventModule<IEventHandlerContext>> modules)
    {
        var voiceModules = modules.OfType<VoicePresenceModule>().ToList();
        if (voiceModules.Count == 0)
            return null;

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
