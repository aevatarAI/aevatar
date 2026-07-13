using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class UserAgentCatalogCredentialRepairPortTests
{
    [Fact]
    public async Task RepairMissingSecretReferenceAsync_ReturnsAcceptedReceiptWithoutWaitingForCommit()
    {
        var fixture = new Fixture(actorExists: true);

        var receipt = await fixture.Port.RepairMissingSecretReferenceAsync(
            " agent-1 ",
            " key-1 ",
            CompleteReference(),
            " key-1 ",
            " restore exact durable reference ",
            " admin-1 ",
            1234);

        receipt.RequestId.Should().NotBeNullOrWhiteSpace();
        receipt.Admission.Accepted.Should().BeTrue();
        receipt.Admission.CommandId.Should().Be("repair-command-1");
        fixture.DispatchedCommand.Should().NotBeNull();
        fixture.DispatchedCommand!.RequestId.Should().Be(receipt.RequestId);
        fixture.DispatchedCommand.AgentId.Should().Be(" agent-1 ");
        fixture.DispatchedCommand.ApiKeyId.Should().Be(" key-1 ");
        fixture.DispatchedCommand.SecretReference.Should().BeEquivalentTo(CompleteReference());
    }

    [Fact]
    public async Task RepairMissingSecretReferenceAsync_CreatesWellKnownActorBeforeDispatchWhenMissing()
    {
        var fixture = new Fixture(actorExists: false);

        await fixture.Port.RepairMissingSecretReferenceAsync(
            "agent-1",
            "key-1",
            CompleteReference(),
            "key-1",
            "restore exact durable reference",
            "admin-1",
            1234);

        await fixture.Runtime.Received(1).CreateAsync<UserAgentCatalogGAgent>(
            UserAgentCatalogGAgent.WellKnownId,
            Arg.Any<CancellationToken>());
        fixture.DispatchedCommand.Should().NotBeNull();
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
        public Fixture(bool actorExists)
        {
            var actor = Substitute.For<IActor>();
            Runtime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
                .Returns(actorExists ? actor : null);
            Runtime.CreateAsync<UserAgentCatalogGAgent>(
                    UserAgentCatalogGAgent.WellKnownId,
                    Arg.Any<CancellationToken>())
                .Returns(actor);
            Dispatch.DispatchAsync(
                    UserAgentCatalogGAgent.WellKnownId,
                    Arg.Any<EventEnvelope>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var envelope = call.ArgAt<EventEnvelope>(1);
                    DispatchedCommand = envelope.Payload.Unpack<UserAgentCatalogRepairCredentialRevocationCommand>();
                    return new DispatchAdmission(
                        true,
                        "repair-command-1",
                        DateTimeOffset.UtcNow,
                        UserAgentCatalogGAgent.WellKnownId,
                        "repair-command-1");
                });

            Port = new UserAgentCatalogCredentialRepairPort(Runtime, Dispatch);
        }

        public IActorRuntime Runtime { get; } = Substitute.For<IActorRuntime>();
        public IActorDispatchPort Dispatch { get; } = Substitute.For<IActorDispatchPort>();
        public UserAgentCatalogCredentialRepairPort Port { get; }
        public UserAgentCatalogRepairCredentialRevocationCommand? DispatchedCommand { get; private set; }
    }
}
