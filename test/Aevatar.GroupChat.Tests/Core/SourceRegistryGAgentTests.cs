using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Core.GAgents;
using Aevatar.GroupChat.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Core;

public sealed class SourceRegistryGAgentTests
{
    [Fact]
    public async Task HandleRegisterAndUpdateTrustAsync_ShouldPersistAndReplaySourceState()
    {
        var eventStore = new InMemoryEventStore();
        var actorId = GroupChatActorIds.Source("doc-1");
        var agent = GroupChatTestKit.CreateStatefulAgent<SourceRegistryGAgent, GroupSourceRegistryState>(
            eventStore,
            actorId,
            static () => new SourceRegistryGAgent());
        await agent.ActivateAsync();

        await agent.HandleRegisterAsync(new RegisterGroupSourceCommand
        {
            SourceId = "doc-1",
            SourceKind = GroupSourceKind.Document,
            CanonicalLocator = "doc://architecture/spec-1",
        });
        await agent.HandleUpdateTrustAsync(new UpdateGroupSourceTrustCommand
        {
            SourceId = "doc-1",
            AuthorityClass = GroupSourceAuthorityClass.InternalAuthoritative,
            VerificationStatus = GroupSourceVerificationStatus.Verified,
        });

        agent.State.SourceId.Should().Be("doc-1");
        agent.State.SourceKind.Should().Be(GroupSourceKind.Document);
        agent.State.CanonicalLocator.Should().Be("doc://architecture/spec-1");
        agent.State.AuthorityClass.Should().Be(GroupSourceAuthorityClass.InternalAuthoritative);
        agent.State.VerificationStatus.Should().Be(GroupSourceVerificationStatus.Verified);
        agent.State.LastAppliedEventVersion.Should().Be(2);
        agent.State.LastEventId.Should().Be("source:doc-1:trust-updated");

        await agent.DeactivateAsync();

        var replayed = GroupChatTestKit.CreateStatefulAgent<SourceRegistryGAgent, GroupSourceRegistryState>(
            eventStore,
            actorId,
            static () => new SourceRegistryGAgent());
        await replayed.ActivateAsync();

        replayed.State.SourceId.Should().Be("doc-1");
        replayed.State.AuthorityClass.Should().Be(GroupSourceAuthorityClass.InternalAuthoritative);
        replayed.State.VerificationStatus.Should().Be(GroupSourceVerificationStatus.Verified);
    }

    [Fact]
    public async Task HandleRegisterAsync_ShouldRejectDuplicateSource()
    {
        var agent = GroupChatTestKit.CreateStatefulAgent<SourceRegistryGAgent, GroupSourceRegistryState>(
            new InMemoryEventStore(),
            GroupChatActorIds.Source("doc-1"),
            static () => new SourceRegistryGAgent());
        await agent.HandleRegisterAsync(new RegisterGroupSourceCommand
        {
            SourceId = "doc-1",
            SourceKind = GroupSourceKind.Document,
            CanonicalLocator = "doc://architecture/spec-1",
        });

        var act = () => agent.HandleRegisterAsync(new RegisterGroupSourceCommand
        {
            SourceId = "doc-1",
            SourceKind = GroupSourceKind.Document,
            CanonicalLocator = "doc://architecture/spec-1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }
}
