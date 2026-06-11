using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Runtime-backed command port for voice demo NyxID chat agent initialization.
/// </summary>
// Refactor (iter34/cluster-005-mainnet-host-direct-actor-runtime):
//   Old pattern: Mainnet Host endpoints inject IActorRuntime/IActorDispatchPort and build EventEnvelope + dispatch directly in Host code.
//   New principle: Host calls Application command ports that normalize, resolve target, build envelope, dispatch, return honest accepted receipt.
//   Host endpoint stays minimal (auth + body parsing). NO direct dependency on IActorRuntime/IActorDispatchPort in Host.
internal sealed class VoiceDemoAgentCommandPort : IVoiceDemoAgentCommandPort
{
    private const string PublisherActorId = "voice-demo-bootstrap";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;

    public VoiceDemoAgentCommandPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
    }

    public async Task<VoiceDemoAgentCommandAcceptedReceipt> EnsureAsync(
        string scopeId,
        string voiceModuleName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("scopeId is required.", nameof(scopeId));
        if (string.IsNullOrWhiteSpace(voiceModuleName))
            throw new ArgumentException("voiceModuleName is required.", nameof(voiceModuleName));

        var actorId = NyxIdChatServiceDefaults.BuildVoiceDemoActorId(scopeId);
        var actor = await _actorRuntime.CreateAsync<NyxIdChatGAgent>(actorId, ct);
        var initialize = new InitializeRoleAgentEvent
        {
            RoleId = "voice-demo",
            RoleName = "Voice Demo Agent",
            ProviderName = NyxIdChatServiceDefaults.ProviderName,
            SystemPrompt = "You are the Aevatar voice demo agent. Reply conversationally and keep spoken answers concise.",
            MaxHistoryMessages = 16,
            EventModules = voiceModuleName.Trim(),
        };
        var commandId = Guid.NewGuid().ToString("N");
        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(initialize),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, actor.Id),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = commandId,
            },
            Runtime = new EnvelopeRuntime
            {
                Deduplication = new DeliveryDeduplication
                {
                    OperationId = commandId,
                },
            },
        };

        await _actorDispatchPort.DispatchAsync(actor.Id, envelope, ct);
        return new VoiceDemoAgentCommandAcceptedReceipt(actor.Id, commandId, commandId);
    }

}
