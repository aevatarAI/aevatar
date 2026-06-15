using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Voice;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Core.Voice;

public sealed class VoicePresenceCapabilityCommandPort : IVoicePresenceCapabilityCommandPort
{
    private const string PublisherActorId = "voice-presence.capability";
    private const string AcceptedStage = "accepted_for_dispatch";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _dispatchPort;

    public VoicePresenceCapabilityCommandPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort dispatchPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task<VoicePresenceCapabilityAcceptedReceipt> EnableAsync(
        string actorId,
        VoicePresenceEnableRequested command,
        CancellationToken ct = default)
    {
        var normalizedActorId = NormalizeRequired(actorId, nameof(actorId));
        ArgumentNullException.ThrowIfNull(command);

        var normalizedModuleName = NormalizeRequired(command.ModuleName, nameof(command.ModuleName));
        var normalizedCommand = command.Clone();
        normalizedCommand.ModuleName = normalizedModuleName;
        normalizedCommand.VoiceSessionDefaults ??= new();

        if (!await _actorRuntime.ExistsAsync(normalizedActorId))
        {
            throw new VoicePresenceCapabilityCommandException(
                VoicePresenceCapabilityCommandError.ActorNotFound,
                $"Actor '{normalizedActorId}' was not found.");
        }

        var commandId = Guid.NewGuid().ToString("N");
        var envelope = new EventEnvelope
        {
            Id = commandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(normalizedCommand),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, normalizedActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = commandId,
            },
        };

        DispatchAdmission admission;
        try
        {
            admission = await _dispatchPort.DispatchAsync(normalizedActorId, envelope, ct);
        }
        catch (Exception ex)
        {
            throw new VoicePresenceCapabilityCommandException(
                VoicePresenceCapabilityCommandError.DispatchFailed,
                "Voice presence enable command dispatch failed.",
                ex);
        }

        return new VoicePresenceCapabilityAcceptedReceipt(
            admission.ActorId,
            normalizedModuleName,
            admission.CommandId,
            admission.CorrelationId,
            AcceptedStage);
    }

    private static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new VoicePresenceCapabilityCommandException(
                VoicePresenceCapabilityCommandError.InvalidInput,
                $"{paramName} is required.");
        }

        return normalized;
    }
}
