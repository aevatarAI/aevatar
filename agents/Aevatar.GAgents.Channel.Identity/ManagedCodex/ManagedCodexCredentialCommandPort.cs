using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Identity;

internal sealed class ManagedCodexCredentialCommandPort(
    IActorRuntime actorRuntime,
    IActorDispatchPort dispatchPort) : IManagedCodexCredentialCommandPort
{
    private const string PublisherActorId = "channel.identity.managed-codex-credential";
    private readonly IActorRuntime _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
    private readonly IActorDispatchPort _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));

    public Task<DispatchAdmission> CommitProvisionedAsync(
        ManagedCodexCredentialDescriptor credential,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return DispatchAsync(
            credential.Owner,
            new CommitManagedCodexCredentialProvisionedCommand { Credential = credential.Clone() },
            ct);
    }

    public Task<DispatchAdmission> CommitRotatedAsync(
        string expectedPreviousApiKeyId,
        ManagedCodexCredentialDescriptor credential,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPreviousApiKeyId);
        ArgumentNullException.ThrowIfNull(credential);
        return DispatchAsync(
            credential.Owner,
            new CommitManagedCodexCredentialRotatedCommand
            {
                ExpectedPreviousApiKeyId = expectedPreviousApiKeyId.Trim(),
                Credential = credential.Clone(),
            },
            ct);
    }

    public Task<DispatchAdmission> CommitPolicyReconciledAsync(
        string expectedApiKeyId,
        ManagedCodexCredentialDescriptor credential,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedApiKeyId);
        ArgumentNullException.ThrowIfNull(credential);
        return DispatchAsync(
            credential.Owner,
            new CommitManagedCodexCredentialPolicyReconciledCommand
            {
                ExpectedApiKeyId = expectedApiKeyId.Trim(),
                Credential = credential.Clone(),
            },
            ct);
    }

    public Task<DispatchAdmission> CommitRevokedAsync(
        ExternalSubjectRef owner,
        string expectedApiKeyId,
        ManagedCodexCredentialCleanup cleanup,
        DateTimeOffset revokedAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedApiKeyId);
        ArgumentNullException.ThrowIfNull(cleanup);
        return DispatchAsync(
            owner,
            new CommitManagedCodexCredentialRevokedCommand
            {
                Owner = owner.Clone(),
                ExpectedApiKeyId = expectedApiKeyId.Trim(),
                Cleanup = cleanup.Clone(),
                RevokedAt = Timestamp.FromDateTimeOffset(revokedAt.ToUniversalTime()),
            },
            ct);
    }

    public Task<DispatchAdmission> QueueCleanupAsync(
        ExternalSubjectRef owner,
        ManagedCodexCredentialCleanup cleanup,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        return DispatchAsync(
            owner,
            new QueueManagedCodexCredentialCleanupCommand
            {
                Owner = owner.Clone(),
                Cleanup = cleanup.Clone(),
            },
            ct);
    }

    public Task<DispatchAdmission> CompleteCleanupTrackAsync(
        ExternalSubjectRef owner,
        string apiKeyId,
        ManagedCodexCredentialCleanupTrack track,
        DateTimeOffset completedAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeyId);
        if (track == ManagedCodexCredentialCleanupTrack.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(track));
        return DispatchAsync(
            owner,
            new CompleteManagedCodexCredentialCleanupTrackCommand
            {
                Owner = owner.Clone(),
                ApiKeyId = apiKeyId.Trim(),
                Track = track,
                CompletedAt = Timestamp.FromDateTimeOffset(completedAt.ToUniversalTime()),
            },
            ct);
    }

    private async Task<DispatchAdmission> DispatchAsync(
        ExternalSubjectRef owner,
        IMessage command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var actorId = ManagedCodexCredentialActorIdentity.From(owner);
        _ = await _actorRuntime.GetAsync(actorId)
            ?? await _actorRuntime.CreateAsync<ManagedCodexCredentialGAgent>(actorId, ct);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, actorId),
        };
        return await _dispatchPort.DispatchAsync(actorId, envelope, ct);
    }
}
