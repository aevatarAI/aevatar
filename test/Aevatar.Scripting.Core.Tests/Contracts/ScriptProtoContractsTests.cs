using FluentAssertions;
using Google.Protobuf.Collections;
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
            SourceText = "return 1;",
            SourceHash = "hash-1",
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
        state.SourceText.Should().Be("return 1;");
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
    public void ScriptRuntimeState_ShouldContainRunFacts()
    {
        var stateType = typeof(ScriptRuntimeState);
        var statePayloadsProperty = stateType.GetProperty("StatePayloads");
        var readModelPayloadsProperty = stateType.GetProperty("ReadModelPayloads");

        statePayloadsProperty.Should().NotBeNull("runtime state must expose keyed state payloads");
        readModelPayloadsProperty.Should().NotBeNull("runtime state must expose keyed readmodel payloads");

        statePayloadsProperty!.PropertyType.Should().Be(typeof(MapField<string, Any>));
        readModelPayloadsProperty!.PropertyType.Should().Be(typeof(MapField<string, Any>));
    }

    [Fact]
    public void ScriptRuntimeState_ShouldSupportStatelessModeByDefault()
    {
        var state = new ScriptRuntimeState
        {
            DefinitionActorId = "definition-1",
            Revision = "rev-1",
            LastAppliedSchemaVersion = "2",
            LastSchemaHash = "schema-hash-1",
            LastRunId = "run-1",
        };

        var stateType = state.GetType();
        var statePayloadsProperty = stateType.GetProperty("StatePayloads");
        var readModelPayloadsProperty = stateType.GetProperty("ReadModelPayloads");

        statePayloadsProperty.Should().NotBeNull();
        readModelPayloadsProperty.Should().NotBeNull();

        state.DefinitionActorId.Should().Be("definition-1");
        state.Revision.Should().Be("rev-1");
        state.LastAppliedSchemaVersion.Should().Be("2");
        state.LastSchemaHash.Should().Be("schema-hash-1");
        state.LastRunId.Should().Be("run-1");

        var statePayloads = (MapField<string, Any>)statePayloadsProperty!.GetValue(state)!;
        var readModelPayloads = (MapField<string, Any>)readModelPayloadsProperty!.GetValue(state)!;
        statePayloads.Count.Should().Be(0);
        readModelPayloads.Count.Should().Be(0);
    }
}
