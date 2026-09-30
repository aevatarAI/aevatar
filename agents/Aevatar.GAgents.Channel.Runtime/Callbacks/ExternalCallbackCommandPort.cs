using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>Normalizes callback writes and dispatches them to the operation inbox with honest admission receipts.</summary>
public sealed class ExternalCallbackCommandPort(
    IActorRuntime runtime, IActorDispatchPort dispatch,
    IExternalCallbackQueryPort query) : IExternalCallbackCommandPort
{
    public async Task<ExternalCallbackRegistration> AdmitAsync(ExternalCallbackRegistration registration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var admitted = registration.Clone();
        if (string.IsNullOrWhiteSpace(admitted.OperationActorId)) admitted.OperationActorId = Guid.NewGuid().ToString("N");
        var commandId = Guid.NewGuid().ToString("N");
        await runtime.CreateAsync<ExternalCallbackGAgent>(admitted.OperationActorId, ct);
        await DispatchAsync(admitted.OperationActorId, commandId,
            new RegisterExternalCallback { Registration = admitted, CommandId = commandId }, ct);
        return admitted;
    }

    public async Task SubmitOAuthAsync(OAuthContinuationSubmission submission, CancellationToken ct = default)
    {
        var target = await ResolveAsync(submission.CallbackId, null, ct);
        if (target.Snapshot.Kind != ExternalCallbackKind.Oauth) throw new ArgumentException("Callback kind mismatch.");
        await DispatchAsync(target.Id, Guid.NewGuid().ToString("N"), submission, ct);
    }

    public async Task HintAsync(string? callbackId, string externalRequestId, CancellationToken ct = default)
    {
        var target = await ResolveAsync(callbackId, externalRequestId, ct);
        if (target.Snapshot.Kind != ExternalCallbackKind.ConnectLink) throw new ArgumentException("Callback kind mismatch.");
        await DispatchAsync(target.Id, Guid.NewGuid().ToString("N"), new ExternalCallbackHint
        { CallbackId = target.CallbackId, ExternalRequestId = externalRequestId ?? "" }, ct);
    }

    private async Task<ExternalCallbackCurrentStateDocument> ResolveAsync(string? callbackId, string? externalId, CancellationToken ct) =>
        await query.FindAsync(callbackId, externalId, ct) ?? throw new KeyNotFoundException("Callback registration is not materialized.");

    private async Task DispatchAsync(string actorId, string commandId, IMessage command, CancellationToken ct)
    {
        var receipt = await dispatch.DispatchAsync(actorId, new EventEnvelope
        {
            Id = commandId, Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect("channel-external-callback-admission", actorId),
        }, ct);
        if (!receipt.Accepted) throw new InvalidOperationException("External callback command was not admitted.");
    }
}
