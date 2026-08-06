using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.CommandServices;

/// <summary>
/// Dispatches user-config write commands to the <see cref="UserConfigGAgent"/>.
/// Uses <see cref="IStudioActorBootstrap"/> so the actor is created if absent
/// before we dispatch the command through <see cref="IActorDispatchPort"/>.
/// </summary>
internal sealed class ActorDispatchUserConfigCommandService : IUserConfigCommandService
{
    private const string DirectRoute = "aevatar.studio.projection.user-config";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly IActorDispatchPort _dispatchPort;

    public ActorDispatchUserConfigCommandService(
        IStudioActorBootstrap bootstrap,
        IActorDispatchPort dispatchPort)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public Task<UserConfigSaveReceipt> UpdateAsync(
        UserConfigResourceKey resource,
        UserConfigUpdate update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var command = new UpdateUserConfigCommand();
        if (update.LlmSelection is not null)
        {
            LLMSelectionPolicy.ValidateSelection(update.LlmSelection);
            command.LlmSelection = update.LlmSelection.Clone();
        }
        if (update.RuntimeMode is not null)
            command.RuntimeMode = update.RuntimeMode;
        if (update.LocalRuntimeBaseUrl is not null)
            command.LocalRuntimeBaseUrl = update.LocalRuntimeBaseUrl;
        if (update.RemoteRuntimeBaseUrl is not null)
            command.RemoteRuntimeBaseUrl = update.RemoteRuntimeBaseUrl;
        if (update.GithubUsername is not null)
            command.GithubUsername = update.GithubUsername;
        if (update.MaxToolRounds.HasValue)
            command.MaxToolRounds = update.MaxToolRounds.Value;

        return DispatchAsync(resource, command, ct);
    }

    private async Task<UserConfigSaveReceipt> DispatchAsync(
        UserConfigResourceKey resource,
        IMessage payload,
        CancellationToken ct)
    {
        var actorId = UserConfigActorIdMapper.Build(resource);
        // Refactor (iter56/cluster-910-projection-activation-cleanup):
        //   old=command-path pre-dispatch activation
        //   new=committed-state plan provider
        //   user-config commands no longer synchronously start materialization.
        var actor = await _bootstrap.EnsureAsync<UserConfigGAgent>(actorId, ct);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(DirectRoute, actor.Id),
        };

        var admission = await _dispatchPort.DispatchAsync(actor.Id, envelope, ct).ConfigureAwait(false);

        return new UserConfigSaveReceipt(
            Accepted: admission.Accepted,
            CommandId: admission.CommandId,
            AckStage: admission.Accepted
                ? UserConfigCommandAckStage.Accepted
                : UserConfigCommandAckStage.AdmissionRejected,
            ActorId: admission.ActorId,
            CorrelationId: admission.CorrelationId,
            AckedAtUtc: admission.AckedAt);
    }
}
