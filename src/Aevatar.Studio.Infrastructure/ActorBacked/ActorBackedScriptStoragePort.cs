using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ScriptStorage;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Actor-backed implementation of <see cref="IScriptStoragePort"/>.
/// Writes go through <see cref="ScriptStorageGAgent"/> event handlers.
///
/// This port is write-only — no readmodel subscription is needed since
/// the interface only exposes <see cref="IScriptStoragePort.UploadScriptAsync"/>.
/// </summary>
internal sealed class ActorBackedScriptStoragePort : IScriptStoragePort
{
    private const string ScriptStorageActorId = "script-storage";

    private readonly IActorRuntime _runtime;
    private readonly ILogger<ActorBackedScriptStoragePort> _logger;

    public ActorBackedScriptStoragePort(
        IActorRuntime runtime,
        ILogger<ActorBackedScriptStoragePort> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task UploadScriptAsync(string scriptId, string sourceText, CancellationToken ct)
    {
        var actor = await EnsureActorAsync(ct);
        var evt = new ScriptUploadedEvent
        {
            ScriptId = scriptId,
            SourceText = sourceText,
        };
        await SendCommandAsync(actor, evt, ct);

        _logger.LogDebug("Script {ScriptId} uploaded to actor-backed storage", scriptId);
    }

    private async Task<IActor> EnsureActorAsync(CancellationToken ct)
    {
        var actor = await _runtime.GetAsync(ScriptStorageActorId);
        if (actor is not null)
            return actor;

        return await _runtime.CreateAsync<ScriptStorageGAgent>(ScriptStorageActorId, ct);
    }

    private static async Task SendCommandAsync(IActor actor, IMessage command, CancellationToken ct)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = new EnvelopeRoute
            {
                Direct = new DirectRoute { TargetActorId = actor.Id },
            },
        };
        await actor.HandleEventAsync(envelope, ct);
    }
}
