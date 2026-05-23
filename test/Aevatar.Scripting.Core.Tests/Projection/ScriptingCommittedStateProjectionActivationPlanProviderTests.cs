using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Projection.Orchestration;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

// Refactor (iter49/issue-882-script-command-readmodel-activation):
//   Old pattern: ScopeScriptCommandApplicationService.UpsertAsync explicitly activated definition/catalog readmodels via ActivateAsync before write commands.
//   New principle: Command service dispatches accepted-only write commands; readmodel activation is owned by scripting committed-state projection activation plan provider.
public sealed class ScriptingCommittedStateProjectionActivationPlanProviderTests
{
    [Fact]
    public void GetPlans_ShouldMapScriptDefinitionUpsertedToAuthorityMaterializationScope()
    {
        var provider = new ScriptingCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
            typeof(ScriptDefinitionGAgent),
            new ScriptDefinitionUpsertedEvent
            {
                ScriptId = "my-script",
                ScriptRevision = "rev-1",
            },
            "user-script-definition:scope-1:my-script:rev-1")).ToArray();

        plans.Should().ContainSingle();
        plans[0].LeaseType.Should().Be(typeof(ScriptAuthorityRuntimeLease));
        plans[0].StartRequest.RootActorId.Should().Be("user-script-definition:scope-1:my-script:rev-1");
        plans[0].StartRequest.ProjectionKind.Should().Be("script-authority-read-model");
        plans[0].StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public void GetPlans_ShouldMapScriptCatalogAuthorityMutationsToAuthorityMaterializationScope()
    {
        var provider = new ScriptingCommittedStateProjectionActivationPlanProvider();

        var promoted = provider.GetPlans(BuildContext(
            typeof(ScriptCatalogGAgent),
            new ScriptCatalogRevisionPromotedEvent
            {
                ScriptId = "my-script",
                Revision = "rev-1",
            },
            "user-script-catalog:scope-1")).ToArray();
        var rollbackRequested = provider.GetPlans(BuildContext(
            typeof(ScriptCatalogGAgent),
            new ScriptCatalogRollbackRequestedEvent
            {
                ScriptId = "my-script",
                TargetRevision = "rev-0",
            },
            "user-script-catalog:scope-1")).ToArray();
        var rolledBack = provider.GetPlans(BuildContext(
            typeof(ScriptCatalogGAgent),
            new ScriptCatalogRolledBackEvent
            {
                ScriptId = "my-script",
                TargetRevision = "rev-0",
            },
            "user-script-catalog:scope-1")).ToArray();

        promoted.Should().ContainSingle();
        rollbackRequested.Should().ContainSingle();
        rolledBack.Should().ContainSingle();
        promoted[0].LeaseType.Should().Be(typeof(ScriptAuthorityRuntimeLease));
        promoted[0].StartRequest.RootActorId.Should().Be("user-script-catalog:scope-1");
        promoted[0].StartRequest.ProjectionKind.Should().Be("script-authority-read-model");
    }

    [Fact]
    public void GetPlans_ShouldNotMatchUnrelatedActorOrStateEvent()
    {
        var provider = new ScriptingCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(BuildContext(
                typeof(ScriptDefinitionGAgent),
                new StringValue { Value = "not-scripting" },
                "user-script-definition:scope-1:my-script:rev-1"))
            .Should().BeEmpty();
        provider.GetPlans(BuildContext(
                typeof(string),
                new ScriptDefinitionUpsertedEvent { ScriptId = "my-script", ScriptRevision = "rev-1" },
                "user-script-definition:scope-1:my-script:rev-1"))
            .Should().BeEmpty();
    }

    private static CommittedStatePublicationContext BuildContext(
        System.Type actorType,
        IMessage evt,
        string actorId) =>
        new()
        {
            ActorId = actorId,
            ActorType = actorType,
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = actorId,
                    EventId = "evt-1",
                    EventData = Any.Pack(evt),
                },
                StateRoot = Any.Pack(new StringValue { Value = "state" }),
            },
        };
}
