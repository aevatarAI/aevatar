using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
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
    // Refactor (iter149/cluster-1133): Old pattern: scripting execution projection tests covered only domain facts.  New principle: actor-owned run outcome events activate the same execution projection path.
    [Fact]
    public void GetPlans_ShouldRejectNullContext()
    {
        var provider = new ScriptingCommittedStateProjectionActivationPlanProvider();

        var act = () => provider.GetPlans(null!).ToArray();

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetPlans_ShouldIgnoreMissingStateEventPayload()
    {
        var provider = new ScriptingCommittedStateProjectionActivationPlanProvider();

        provider.GetPlans(new CommittedStatePublicationContext
            {
                ActorId = "user-script-definition:scope-1:my-script:rev-1",
                ActorType = typeof(ScriptDefinitionGAgent),
                Published = new CommittedStateEventPublished(),
            })
            .Should().BeEmpty();

        provider.GetPlans(new CommittedStatePublicationContext
            {
                ActorId = "user-script-definition:scope-1:my-script:rev-1",
                ActorType = typeof(ScriptDefinitionGAgent),
                Published = new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        AgentId = "user-script-definition:scope-1:my-script:rev-1",
                        EventId = "evt-1",
                    },
                },
            })
            .Should().BeEmpty();
    }

    [Fact]
    public void GetPlans_ShouldMapScriptDefinitionAuthorityMutationsToAuthorityMaterializationScope()
    {
        var provider = new ScriptingCommittedStateProjectionActivationPlanProvider();

        IMessage[] mutationEvents =
        [
            new ScriptDefinitionUpsertedEvent
            {
                ScriptId = "my-script",
                ScriptRevision = "rev-1",
            },
            new ScriptReadModelSchemaDeclaredEvent
            {
                ScriptId = "my-script",
                ScriptRevision = "rev-1",
            },
            new ScriptReadModelSchemaValidatedEvent
            {
                ScriptId = "my-script",
                ScriptRevision = "rev-1",
            },
            new ScriptReadModelSchemaActivationFailedEvent
            {
                ScriptId = "my-script",
                ScriptRevision = "rev-1",
            },
        ];

        var plans = mutationEvents
            .Select(evt => provider.GetPlans(BuildContext(
                typeof(ScriptDefinitionGAgent),
                evt,
                "user-script-definition:scope-1:my-script:rev-1")).ToArray())
            .ToArray();

        plans.Should().OnlyContain(plan => plan.Length == 1);
        plans.Select(plan => plan[0].LeaseType)
            .Should().OnlyContain(leaseType => leaseType == typeof(ScriptAuthorityRuntimeLease));
        plans.Select(plan => plan[0].StartRequest.RootActorId)
            .Should().OnlyContain(actorId => actorId == "user-script-definition:scope-1:my-script:rev-1");
        plans.Select(plan => plan[0].StartRequest.ProjectionKind)
            .Should().OnlyContain(kind => kind == "script-authority-read-model");
        plans.Select(plan => plan[0].StartRequest.Mode)
            .Should().OnlyContain(mode => mode == ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public void GetPlans_ShouldMapScriptDomainFactCommittedToExecutionMaterializationScope()
    {
        var provider = new ScriptingCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
            typeof(ScriptBehaviorGAgent),
            new ScriptDomainFactCommitted
            {
                ActorId = "script-runtime:scope-1:my-script",
                ScriptId = "my-script",
                Revision = "rev-1",
            },
            "script-runtime:scope-1:my-script")).ToArray();

        plans.Should().ContainSingle();
        plans[0].LeaseType.Should().Be(typeof(ScriptExecutionMaterializationRuntimeLease));
        plans[0].StartRequest.RootActorId.Should().Be("script-runtime:scope-1:my-script");
        plans[0].StartRequest.ProjectionKind.Should().Be("script-execution-read-model");
        plans[0].StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public void GetPlans_ShouldMapScriptRunOutcomeRecordedToExecutionMaterializationScope()
    {
        var provider = new ScriptingCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext(
            typeof(ScriptBehaviorGAgent),
            new ScriptRunOutcomeRecordedEvent
            {
                ActorId = "script-runtime:scope-1:my-script",
                ScriptId = "my-script",
                ScriptRevision = "rev-1",
                ScriptRunId = "run-1",
                Status = ScriptRunOutcomeStatus.Succeeded,
            },
            "script-runtime:scope-1:my-script")).ToArray();

        plans.Should().ContainSingle();
        plans[0].LeaseType.Should().Be(typeof(ScriptExecutionMaterializationRuntimeLease));
        plans[0].StartRequest.RootActorId.Should().Be("script-runtime:scope-1:my-script");
        plans[0].StartRequest.ProjectionKind.Should().Be("script-execution-read-model");
        plans[0].StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public void GetPlans_ShouldMapScriptEvolutionSessionMutationsToEvolutionMaterializationScope()
    {
        var provider = new ScriptingCommittedStateProjectionActivationPlanProvider();

        IMessage[] mutationEvents =
        [
            new ScriptEvolutionSessionStartedEvent { ProposalId = "proposal-1" },
            new ScriptEvolutionProposedEvent { ProposalId = "proposal-1" },
            new ScriptEvolutionBuildRequestedEvent { ProposalId = "proposal-1" },
            new ScriptEvolutionValidatedEvent { ProposalId = "proposal-1" },
            new ScriptEvolutionRejectedEvent { ProposalId = "proposal-1" },
            new ScriptEvolutionPromotedEvent { ProposalId = "proposal-1" },
            new ScriptEvolutionRollbackRequestedEvent { ProposalId = "proposal-1" },
            new ScriptEvolutionRolledBackEvent { ProposalId = "proposal-1" },
            new ScriptEvolutionSessionCompletedEvent { ProposalId = "proposal-1", Status = "promoted" },
        ];

        var plans = mutationEvents
            .Select(evt => provider.GetPlans(BuildContext(
                typeof(ScriptEvolutionSessionGAgent),
                evt,
                "script-evolution-session:scope-1:proposal-1")).ToArray())
            .ToArray();

        plans.Should().OnlyContain(plan => plan.Length == 1);
        plans.Select(plan => plan[0].LeaseType)
            .Should().OnlyContain(leaseType => leaseType == typeof(ScriptEvolutionMaterializationRuntimeLease));
        plans.Select(plan => plan[0].StartRequest.RootActorId)
            .Should().OnlyContain(actorId => actorId == "script-evolution-session:scope-1:proposal-1");
        plans.Select(plan => plan[0].StartRequest.ProjectionKind)
            .Should().OnlyContain(kind => kind == "script-evolution-read-model");
        plans.Select(plan => plan[0].StartRequest.Mode)
            .Should().OnlyContain(mode => mode == ProjectionRuntimeMode.DurableMaterialization);
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
        provider.GetPlans(BuildContext(
                typeof(ScriptCatalogGAgent),
                new StringValue { Value = "not-catalog-authority-mutation" },
                "user-script-catalog:scope-1"))
            .Should().BeEmpty();
        provider.GetPlans(BuildContext(
                typeof(ScriptEvolutionSessionGAgent),
                new StringValue { Value = "not-evolution-mutation" },
                "script-evolution-session:scope-1:proposal-1"))
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
