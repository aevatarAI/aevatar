using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Persistence;
using FluentAssertions;
using Google.Protobuf;
using NSubstitute;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeActorGrainStorageRecoveryTests
{
    [Fact]
    public async Task InboxDelivery_WhenRuntimeStateNeedsRecovery_ShouldRemainUnacknowledged()
    {
        var state = Substitute.For<IPersistentState<RuntimeActorGrainState>>();
        state.State.Returns(new RuntimeActorGrainState
        {
            StorageRecovery = new RuntimeActorStateStorageRecovery
            {
                Reason = RuntimeActorStateStorageRecoveryReason.LegacyJsonReferenceToken,
                SourcePayload = ByteString.CopyFromUtf8("\"$id\""),
            },
        });
        var publicationState = Substitute.For<
            IPersistentState<RuntimeActorCommittedStatePublicationGrainState>>();
        var grain = new RuntimeActorGrain(state, publicationState);
        var envelope = new EventEnvelope
        {
            Id = "recovery-envelope",
        };

        var delivery = () => grain.HandleEnvelopeAsync(envelope.ToByteArray());

        var failure = await delivery.Should()
            .ThrowAsync<RuntimeActorStateStorageRecoveryRequiredException>();
        failure.Which.RecoveryReason.Should().Be(
            RuntimeActorStateStorageRecoveryReason.LegacyJsonReferenceToken);
        (await grain.IsInitializedAsync()).Should().BeFalse();
        await state.DidNotReceiveWithAnyArgs().WriteStateAsync(default);
    }
}
