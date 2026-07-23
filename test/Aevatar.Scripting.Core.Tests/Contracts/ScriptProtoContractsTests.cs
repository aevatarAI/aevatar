using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Contracts;

public class ScriptProtoContractsTests
{
    [Fact]
    public void ScriptDefinitionState_ShouldContainSourceAndRevision()
    {
        var state = new ScriptDefinitionState
        {
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceHash = "hash-1",
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource("return 1;"),
            ReadModelSchemaVersion = "2",
            ReadModelSchema = Any.Pack(new ScriptReadModelSchemaSpec
            {
                SchemaId = "claim_case",
                SchemaVersion = "2",
            }),
            ReadModelSchemaHash = "schema-hash-1",
            ReadModelSchemaStoreKinds = { "elasticsearch" },
            ReadModelSchemaStatus = "validated",
            ReadModelSchemaFailureReason = string.Empty,
        };

        state.ScriptId.Should().Be("script-1");
        state.Revision.Should().Be("rev-1");
        state.ScriptPackage.GetPrimaryCSharpSource().Should().Be("return 1;");
        state.SourceHash.Should().Be("hash-1");
        state.ReadModelSchemaVersion.Should().Be("2");
        state.ReadModelSchema.Should().NotBeNull();
        state.ReadModelSchema.Is(ScriptReadModelSchemaSpec.Descriptor).Should().BeTrue();
        state.ReadModelSchema.Unpack<ScriptReadModelSchemaSpec>().SchemaId.Should().Be("claim_case");
        state.ReadModelSchemaHash.Should().Be("schema-hash-1");
        state.ReadModelSchemaStoreKinds.Should().Contain("elasticsearch");
        state.ReadModelSchemaStatus.Should().Be("validated");
        state.ReadModelSchemaFailureReason.Should().BeEmpty();
    }

    [Fact]
    public void ScriptBehaviorState_ShouldContainBindingAndStateRoot()
    {
        var stateType = typeof(ScriptBehaviorState);
        var stateRootProperty = stateType.GetProperty("StateRoot");

        stateRootProperty.Should().NotBeNull("behavior state must persist typed state root");
        stateRootProperty!.PropertyType.Should().Be(typeof(Any));
    }

    [Fact]
    public void ScriptBehaviorState_ShouldSupportEmptyStateRootByDefault()
    {
        var state = new ScriptBehaviorState
        {
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            StateTypeUrl = "type.googleapis.com/example.State",
            ReadModelTypeUrl = "type.googleapis.com/example.ReadModel",
            ReadModelSchemaVersion = "2",
            ReadModelSchemaHash = "schema-hash-1",
            LastRunId = "run-1",
        };

        state.DefinitionActorId.Should().Be("definition-1");
        state.ScriptId.Should().Be("script-1");
        state.Revision.Should().Be("rev-1");
        state.StateTypeUrl.Should().Be("type.googleapis.com/example.State");
        state.ReadModelTypeUrl.Should().Be("type.googleapis.com/example.ReadModel");
        state.ReadModelSchemaVersion.Should().Be("2");
        state.ReadModelSchemaHash.Should().Be("schema-hash-1");
        state.LastRunId.Should().Be("run-1");
        state.StateRoot.Should().BeNull();
    }

    [Fact]
    public void ScriptRunOutcomeRecordedEvent_ShouldRoundTripTypedOutcomeFields()
    {
        var outcome = new ScriptRunOutcomeRecordedEvent
        {
            ActorId = "script-runtime:scope-1:script-1",
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            ScriptRevision = "rev-1",
            ScriptRunId = "run-1",
            CommandId = "command-1",
            CorrelationId = "correlation-1",
            ScopeId = "scope-1",
            Status = ScriptRunOutcomeStatus.Succeeded,
            CommittedFactCount = 2,
            StateVersion = 7,
            OccurredAtUnixTimeMs = 123456,
            Result = Any.Pack(new StringValue { Value = "ok" }),
        };

        var parsed = ScriptRunOutcomeRecordedEvent.Parser.ParseFrom(outcome.ToByteArray());

        parsed.ActorId.Should().Be("script-runtime:scope-1:script-1");
        parsed.ScriptRunId.Should().Be("run-1");
        parsed.Status.Should().Be(ScriptRunOutcomeStatus.Succeeded);
        parsed.CommittedFactCount.Should().Be(2);
        parsed.StateVersion.Should().Be(7);
        parsed.OccurredAtUnixTimeMs.Should().Be(123456);
        parsed.Result.Unpack<StringValue>().Value.Should().Be("ok");
    }

    [Fact]
    public void ScriptBehaviorState_ShouldRoundTripRunOutcomeDelivery()
    {
        var state = new ScriptBehaviorState
        {
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            RunOutcomes =
            {
                ["run-1"] = new ScriptRunOutcomeDeliveryState
                {
                    DeliveryId = "delivery-1",
                    Status = ScriptRunOutcomeDeliveryStatus.Prepared,
                    Outcome = new ScriptRunOutcomeRecordedEvent
                    {
                        ScriptRunId = "run-1",
                        CommandId = "command-1",
                        Status = ScriptRunOutcomeStatus.Failed,
                        Error = "failed",
                    },
                },
            },
        };

        var parsed = ScriptBehaviorState.Parser.ParseFrom(state.ToByteArray());

        var delivery = parsed.RunOutcomes.Should().ContainKey("run-1").WhoseValue;
        delivery.DeliveryId.Should().Be("delivery-1");
        delivery.Status.Should().Be(ScriptRunOutcomeDeliveryStatus.Prepared);
        delivery.Outcome.ScriptRunId.Should().Be("run-1");
        delivery.Outcome.CommandId.Should().Be("command-1");
        delivery.Outcome.Status.Should().Be(ScriptRunOutcomeStatus.Failed);
        delivery.Outcome.Error.Should().Be("failed");
    }

    [Fact]
    public void BindScriptBehaviorRequestedEvent_ShouldCarryBindingContractFields()
    {
        var bind = new BindScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceHash = "hash-1",
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource("public sealed class Behavior {}"),
            StateTypeUrl = "type.googleapis.com/example.State",
            ReadModelTypeUrl = "type.googleapis.com/example.ReadModel",
            ReadModelSchemaVersion = "2",
            ReadModelSchemaHash = "schema-hash-1",
            StateDescriptorFullName = "Example.State",
            ReadModelDescriptorFullName = "Example.ReadModel",
            RuntimeSemantics = new ScriptRuntimeSemanticsSpec(),
        };

        bind.DefinitionActorId.Should().Be("definition-1");
        bind.ScriptId.Should().Be("script-1");
        bind.Revision.Should().Be("rev-1");
        bind.SourceHash.Should().Be("hash-1");
        bind.ReadModelSchemaVersion.Should().Be("2");
        bind.StateDescriptorFullName.Should().Be("Example.State");
        bind.ReadModelDescriptorFullName.Should().Be("Example.ReadModel");
        bind.RuntimeSemantics.Messages.Should().BeEmpty();
    }

    [Fact]
    public void ScriptBehaviorBoundEvent_ShouldCarryMaterializedBindingFields()
    {
        var bound = new ScriptBehaviorBoundEvent
        {
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceHash = "hash-1",
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource("public sealed class Behavior {}"),
            StateTypeUrl = "type.googleapis.com/example.State",
            ReadModelTypeUrl = "type.googleapis.com/example.ReadModel",
            ReadModelSchemaVersion = "2",
            ReadModelSchemaHash = "schema-hash-1",
        };

        bound.DefinitionActorId.Should().Be("definition-1");
        bound.ScriptId.Should().Be("script-1");
        bound.Revision.Should().Be("rev-1");
        bound.SourceHash.Should().Be("hash-1");
        bound.ReadModelSchemaHash.Should().Be("schema-hash-1");
    }

    [Fact]
    public void ScriptEvolutionSessionCompletedEvent_ShouldCarryDefinitionBindingSnapshot()
    {
        var completed = new ScriptEvolutionSessionCompletedEvent
        {
            ProposalId = "proposal-1",
            Accepted = true,
            DefinitionActorId = "definition-1",
            DefinitionSnapshot = new ScriptDefinitionBindingSpec
            {
                ScriptId = "script-1",
                Revision = "rev-1",
                SourceHash = "hash-1",
                ReadModelSchemaVersion = "2",
            },
        };

        completed.DefinitionSnapshot.Should().NotBeNull();
        completed.DefinitionSnapshot.ScriptId.Should().Be("script-1");
        completed.DefinitionSnapshot.Revision.Should().Be("rev-1");
        completed.DefinitionSnapshot.SourceHash.Should().Be("hash-1");
        completed.DefinitionSnapshot.ReadModelSchemaVersion.Should().Be("2");
    }
}
