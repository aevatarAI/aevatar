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
    public void ScriptBehaviorBindingMessages_ShouldCarryBindingContractFields()
    {
        var bind = new BindScriptBehaviorRequestedEvent
        {
            DefinitionActorId = "definition-1",
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceText = "public sealed class Behavior {}",
            SourceHash = "hash-1",
            StateTypeUrl = "type.googleapis.com/example.State",
            ReadModelTypeUrl = "type.googleapis.com/example.ReadModel",
            ReadModelSchemaVersion = "2",
            ReadModelSchemaHash = "schema-hash-1",
        };
        var response = new ScriptBehaviorBindingRespondedEvent
        {
            RequestId = "request-1",
            Found = true,
            DefinitionActorId = bind.DefinitionActorId,
            ScriptId = bind.ScriptId,
            Revision = bind.Revision,
            SourceHash = bind.SourceHash,
            StateTypeUrl = bind.StateTypeUrl,
            ReadModelTypeUrl = bind.ReadModelTypeUrl,
            ReadModelSchemaVersion = bind.ReadModelSchemaVersion,
            ReadModelSchemaHash = bind.ReadModelSchemaHash,
        };

        bind.DefinitionActorId.Should().Be("definition-1");
        bind.ScriptId.Should().Be("script-1");
        bind.Revision.Should().Be("rev-1");
        bind.SourceHash.Should().Be("hash-1");
        bind.ReadModelSchemaVersion.Should().Be("2");
        response.RequestId.Should().Be("request-1");
        response.Found.Should().BeTrue();
        response.ReadModelSchemaHash.Should().Be("schema-hash-1");
    }

    [Fact]
    public void QueryScriptBehaviorBindingRequestedEvent_ShouldCarryRequestAndReplyStream()
    {
        var query = new QueryScriptBehaviorBindingRequestedEvent
        {
            RequestId = "request-1",
            ReplyStreamId = "reply-stream",
        };

        query.RequestId.Should().Be("request-1");
        query.ReplyStreamId.Should().Be("reply-stream");
    }
}
