using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class UserAgentCatalogCredentialRepairPortTests
{
    [Fact]
    public async Task RepairMissingSecretReferenceAsync_WaitsForMatchingCommittedSuccess()
    {
        var fixture = new Fixture();
        fixture.CommittedResultFactory = command => new UserAgentCatalogCredentialRevocationRepairedEvent
        {
            RequestId = command.RequestId,
            AgentId = command.AgentId,
            ApiKeyId = command.ApiKeyId,
        };

        var result = await fixture.Port.RepairMissingSecretReferenceAsync(
            "agent-1",
            "key-1",
            CompleteReference(),
            "key-1",
            "restore exact durable reference",
            "admin-1",
            1234);

        result.Repaired.Should().BeTrue();
        result.RejectionReason.Should().Be(UserAgentCatalogCredentialRevocationRepairRejectionReason.Unspecified);
        fixture.DispatchedCommand.Should().NotBeNull();
        fixture.SubscriptionDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task RepairMissingSecretReferenceAsync_ReturnsMatchingCommittedRejection()
    {
        var fixture = new Fixture();
        fixture.CommittedResultFactory = command => new UserAgentCatalogCredentialRevocationRepairRejectedEvent
        {
            RequestId = command.RequestId,
            AgentId = command.AgentId,
            ApiKeyId = command.ApiKeyId,
            Reason = UserAgentCatalogCredentialRevocationRepairRejectionReason.AliasConflict,
        };

        var result = await fixture.Port.RepairMissingSecretReferenceAsync(
            "agent-1",
            "key-1",
            CompleteReference(),
            "key-1",
            "restore exact durable reference",
            "admin-1",
            1234);

        result.Repaired.Should().BeFalse();
        result.RejectionReason.Should().Be(UserAgentCatalogCredentialRevocationRepairRejectionReason.AliasConflict);
    }

    private static SecretReference CompleteReference() => new()
    {
        Ref = "secret-1",
        Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
        OwnerScopeKey = "scheduled-agent:key-1",
        Version = 1,
        Fingerprint = "sha256:test",
    };

    private sealed class Fixture
    {
        private Func<CommittedStateEventPublished, Task>? _handler;

        public Fixture()
        {
            Runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId).Returns(Substitute.For<IActor>());
            SubscriptionProvider.SubscribeAsync<CommittedStateEventPublished>(
                    UserAgentCatalogGAgent.WellKnownId,
                    Arg.Any<Func<CommittedStateEventPublished, Task>>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    _handler = call.ArgAt<Func<CommittedStateEventPublished, Task>>(1);
                    return Task.FromResult<IAsyncDisposable>(
                        new RecordingSubscription(() => SubscriptionDisposed = true));
                });
            Dispatch.DispatchAsync(
                    UserAgentCatalogGAgent.WellKnownId,
                    Arg.Any<EventEnvelope>(),
                    Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var envelope = call.ArgAt<EventEnvelope>(1);
                    DispatchedCommand = envelope.Payload.Unpack<UserAgentCatalogRepairCredentialRevocationCommand>();
                    var committedEvent = CommittedResultFactory(DispatchedCommand);
                    await _handler!(new CommittedStateEventPublished
                    {
                        StateEvent = new StateEvent
                        {
                            EventData = Any.Pack(committedEvent),
                        },
                    });
                    return DispatchAdmissionFactory.Create(UserAgentCatalogGAgent.WellKnownId, envelope);
                });

            Port = new UserAgentCatalogCredentialRepairPort(Runtime, Dispatch, SubscriptionProvider);
        }

        public IActorRuntime Runtime { get; } = Substitute.For<IActorRuntime>();
        public IActorDispatchPort Dispatch { get; } = Substitute.For<IActorDispatchPort>();
        public IActorEventSubscriptionProvider SubscriptionProvider { get; } =
            Substitute.For<IActorEventSubscriptionProvider>();
        public UserAgentCatalogCredentialRepairPort Port { get; }
        public Func<UserAgentCatalogRepairCredentialRevocationCommand, Google.Protobuf.IMessage> CommittedResultFactory
            { get; set; } = null!;
        public UserAgentCatalogRepairCredentialRevocationCommand? DispatchedCommand { get; private set; }
        public bool SubscriptionDisposed { get; private set; }
    }

    private sealed class RecordingSubscription(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            dispose();
            return ValueTask.CompletedTask;
        }
    }
}
