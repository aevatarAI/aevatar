using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserMemory;
using Aevatar.Studio.Application.Studio.Abstractions;
using Google.Protobuf.WellKnownTypes;
using ApplicationRetentionRule = Aevatar.Studio.Application.Studio.Abstractions.UserMemoryCategoryRetentionRule;
using ActorRetentionRule = Aevatar.GAgents.UserMemory.UserMemoryCategoryRetentionRule;
using ActorUserMemoryCategory = Aevatar.GAgents.UserMemory.UserMemoryCategory;

namespace Aevatar.Studio.Projection.CommandServices;

internal sealed class ActorDispatchUserMemoryRetentionPolicyCommandService
    : IUserMemoryRetentionPolicyCommandPort
{
    private const string ActorIdPrefix = "user-memory-";
    private const string DirectRoute = "aevatar.studio.projection.user-memory-retention-policy";

    private readonly IStudioActorBootstrap _bootstrap;
    private readonly IActorDispatchPort _dispatchPort;

    public ActorDispatchUserMemoryRetentionPolicyCommandService(
        IStudioActorBootstrap bootstrap,
        IActorDispatchPort dispatchPort)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task<UserConfigSaveReceipt> ReplaceAsync(
        ReplaceUserMemoryRetentionPolicy command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ExpectedStateVersion < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command.ExpectedStateVersion),
                "Expected state version must be non-negative.");
        }
        if (string.IsNullOrWhiteSpace(command.Owner.ScopeId))
            throw new ArgumentException("User-memory owner scope is required.", nameof(command.Owner));
        if (command.Rules is null)
            throw new ArgumentNullException(nameof(command.Rules));

        var payload = MapCommand(command);
        _ = UserMemoryGAgent.BuildRetentionPolicyReplacedEvent(new UserMemoryState(), payload);

        var actorId = ActorIdPrefix + command.Owner.ScopeId;
        var actor = await _bootstrap.EnsureAsync<UserMemoryGAgent>(actorId, ct);
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(DirectRoute, actor.Id),
        };

        var admission = await _dispatchPort.DispatchAsync(actor.Id, envelope, ct).ConfigureAwait(false);
        return new UserConfigSaveReceipt(
            admission.Accepted,
            admission.CommandId,
            admission.Accepted
                ? UserConfigCommandAckStage.Accepted
                : UserConfigCommandAckStage.AdmissionRejected,
            admission.ActorId,
            admission.CorrelationId,
            admission.AckedAt);
    }

    private static ReplaceUserMemoryRetentionPolicyCommand MapCommand(
        ReplaceUserMemoryRetentionPolicy command)
    {
        var payload = new ReplaceUserMemoryRetentionPolicyCommand
        {
            ExpectedStateVersion = command.ExpectedStateVersion,
            MutationId = command.MutationId ?? string.Empty,
        };
        payload.Rules.AddRange(command.Rules.Select(MapRule));
        return payload;
    }

    private static ActorRetentionRule MapRule(ApplicationRetentionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new ActorRetentionRule
        {
            Category = rule.Category switch
            {
                Aevatar.Studio.Application.Studio.Abstractions.UserMemoryCategory.Preference =>
                    ActorUserMemoryCategory.Preference,
                Aevatar.Studio.Application.Studio.Abstractions.UserMemoryCategory.Instruction =>
                    ActorUserMemoryCategory.Instruction,
                Aevatar.Studio.Application.Studio.Abstractions.UserMemoryCategory.Context =>
                    ActorUserMemoryCategory.Context,
                _ => ActorUserMemoryCategory.Unspecified,
            },
            MaxEntries = rule.MaxEntries,
            EvictionRank = rule.EvictionRank,
        };
    }
}
