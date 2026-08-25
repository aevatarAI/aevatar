using System.Reflection;
using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.ContentArtifacts;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests.ContentArtifacts;

public sealed class ContentArtifactPinGAgentTests
{
    private const string ScopeId = "scope-1";
    private const string PinKey = "daily-ops-report";
    private static readonly string ActorId = ContentArtifactConventions.BuildPinActorId(ScopeId, PinKey);
    private static readonly MethodInfo SetIdMethod = typeof(GAgentBase)
        .GetMethod("SetId", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("GAgentBase.SetId was not found.");

    [Fact]
    public async Task SetReplaceAndClear_ShouldMaintainOnePointerAndMonotonicPinVersion()
    {
        var agent = await CreateAgentAsync();

        await agent.HandleSetAsync(Set("artifact-a", 0, "mutation-1"));
        await agent.HandleSetAsync(Set("artifact-b", 1, "mutation-2"));
        await agent.HandleClearAsync(Clear(2, "mutation-3"));

        agent.State.ScopeId.Should().Be(ScopeId);
        agent.State.PinKey.Should().Be(PinKey);
        agent.State.PinnedArtifactId.Should().BeEmpty();
        agent.State.PinnedBy.Should().BeNull();
        agent.State.PinVersion.Should().Be(3);
        agent.State.LastMutationId.Should().Be("mutation-3");
        agent.State.LastMutationStatus.Should().Be(ContentArtifactPinMutationStatus.Succeeded);
    }

    [Fact]
    public async Task MutationReplay_ShouldBeIdempotentAndRejectDifferentFacts()
    {
        var agent = await CreateAgentAsync();
        var command = Set("artifact-a", 0, "mutation-1");

        await agent.HandleSetAsync(command);
        await agent.HandleSetAsync(command.Clone());

        agent.State.PinVersion.Should().Be(1);
        var conflicting = command.Clone();
        conflicting.ArtifactId = "artifact-b";
        var act = () => agent.HandleSetAsync(conflicting);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*mutation_id*already used for different facts*");
        agent.State.PinnedArtifactId.Should().Be("artifact-a");
    }

    [Fact]
    public async Task CasConflict_ShouldPersistRejectionWithoutChangingPointerAndReplayDeterministically()
    {
        var store = new InMemoryEventStore();
        var agent = await CreateAgentAsync(store);
        await agent.HandleSetAsync(Set("artifact-a", 0, "mutation-1"));
        var stale = Set("artifact-b", 0, "mutation-stale");

        await agent.HandleSetAsync(stale);
        await agent.HandleSetAsync(stale.Clone());

        agent.State.PinnedArtifactId.Should().Be("artifact-a");
        agent.State.PinVersion.Should().Be(1);
        agent.State.LastMutationId.Should().Be("mutation-stale");
        agent.State.LastMutationStatus.Should().Be(ContentArtifactPinMutationStatus.Rejected);
        agent.State.LastRejectionCode.Should().Be(ContentArtifactPinRejectionCode.PinVersionConflict);

        var recovered = await CreateAgentAsync(store);
        recovered.State.Should().BeEquivalentTo(agent.State);
    }

    [Fact]
    public async Task PinKeyAndActorAddress_ShouldUseCanonicalLabelKeyRules()
    {
        var invalidKey = () => ContentArtifactConventions.BuildPinActorId(ScopeId, "aevatar.primary");
        invalidKey.Should().Throw<ArgumentException>().WithMessage("*reserved*");

        var wrongAddress = await CreateAgentAsync(actorId: "content-artifact-pin:scope-1:other-key");
        var act = () => wrongAddress.HandleSetAsync(Set("artifact-a", 0, "mutation-1"));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*canonical identity*");
    }

    private static async Task<ContentArtifactPinGAgent> CreateAgentAsync(
        InMemoryEventStore? eventStore = null,
        string? actorId = null)
    {
        var agent = new ContentArtifactPinGAgent
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ContentArtifactPinState>(
                eventStore ?? new InMemoryEventStore()),
            Services = new ServiceCollection()
                .AddSingleton<IEnumerable<IGAgentExecutionHook>>([])
                .BuildServiceProvider(),
        };
        SetIdMethod.Invoke(agent, [actorId ?? ActorId]);
        await agent.ActivateAsync();
        return agent;
    }

    private static SetContentArtifactPinCommand Set(
        string artifactId,
        long expectedPinVersion,
        string mutationId) =>
        new()
        {
            ScopeId = ScopeId,
            PinKey = PinKey,
            ArtifactId = artifactId,
            RequestedBy = Principal(),
            ExpectedPinVersion = expectedPinVersion,
            MutationId = mutationId,
        };

    private static ClearContentArtifactPinCommand Clear(long expectedPinVersion, string mutationId) =>
        new()
        {
            ScopeId = ScopeId,
            PinKey = PinKey,
            RequestedBy = Principal(),
            ExpectedPinVersion = expectedPinVersion,
            MutationId = mutationId,
        };

    private static ContentArtifactPrincipal Principal() =>
        new() { PrincipalId = "owner-1", PrincipalKind = "user" };
}
