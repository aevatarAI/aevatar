using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.ReadModels;
using Aevatar.Scripting.Core.Tests.Messages;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptReadModelDocumentJsonConverterTests
{
    [Fact]
    public void ReadModelPayload_ShouldRoundTripThroughJson_WithBase64Encoding()
    {
        var document = new ScriptReadModelDocument
        {
            Id = "runtime-1",
            ReadModelTypeUrl = Any.Pack(new SimpleTextReadModel()).TypeUrl,
            ReadModelPayload = Any.Pack(new SimpleTextReadModel
            {
                HasValue = true,
                Value = "HELLO",
            }),
            StateVersion = 3,
        };

        var json = JsonSerializer.Serialize(document);
        var restored = JsonSerializer.Deserialize<ScriptReadModelDocument>(json);

        json.Should().Contain("\"type_url\"");
        json.Should().Contain("\"payload_base64\"");
        restored.Should().NotBeNull();
        restored!.ReadModelPayload.Should().NotBeNull();
        restored.ReadModelPayload!.Unpack<SimpleTextReadModel>().Value.Should().Be("HELLO");
    }
}

public sealed class ScriptNativeDocumentReadModelJsonConverterTests
{
    [Fact]
    public void NativeDocument_ShouldRoundTripThroughJson_WithStructuredFields()
    {
        var updatedAt = DateTimeOffset.Parse("2026-03-16T16:05:00+00:00");
        var document = new ScriptNativeDocumentReadModel
        {
            Id = "runtime-1",
            ScriptId = "script-1",
            DefinitionActorId = "definition-1",
            Revision = "rev-1",
            SchemaId = "profile",
            SchemaVersion = "3",
            SchemaHash = "hash-1",
            DocumentIndexScope = "scope-1",
            StateVersion = 7,
            LastEventId = "evt-1",
            UpdatedAt = updatedAt,
            Fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["text"] = "hello",
                ["number"] = 42L,
                ["date"] = updatedAt,
                ["nested"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["flag"] = true,
                },
                ["list"] = new List<object?> { "a", 2L, false },
            },
        };

        var json = JsonSerializer.Serialize(document);
        var restored = JsonSerializer.Deserialize<ScriptNativeDocumentReadModel>(json);

        json.Should().Contain("\"fields\"");
        restored.Should().NotBeNull();
        restored!.Fields["text"].Should().Be("hello");
        restored.Fields["number"].Should().Be(42L);
        restored.Fields["date"].Should().Be(updatedAt);
        restored.Fields["nested"].Should().BeAssignableTo<IDictionary<string, object?>>();
        restored.Fields["list"].Should().BeAssignableTo<IReadOnlyList<object?>>();
        restored.StateVersion.Should().Be(7);
        restored.LastEventId.Should().Be("evt-1");
    }

    [Fact]
    public void NativeDocument_ShouldReadDefaults_WhenJsonOmitsFields_OrIsNull()
    {
        const string json =
            """
            {
              "id": " runtime-2 ",
              "schema_id": " profile ",
              "fields": {
                "obj": { "n": 1 },
                "arr": [1, 2.5, "x"],
                "date": "2026-03-16T00:00:00+00:00",
                "true": true,
                "false": false,
                "null": null
              }
            }
            """;

        var restored = JsonSerializer.Deserialize<ScriptNativeDocumentReadModel>(json);
        var nullRestored = JsonSerializer.Deserialize<ScriptNativeDocumentReadModel>("null");

        restored.Should().NotBeNull();
        restored!.Id.Should().Be("runtime-2");
        restored.SchemaId.Should().Be("profile");
        restored.SchemaVersion.Should().BeEmpty();
        restored.StateVersion.Should().Be(0);
        restored.Fields["obj"].Should().BeAssignableTo<IDictionary<string, object?>>();
        restored.Fields["arr"].Should().BeAssignableTo<IReadOnlyList<object?>>();
        restored.Fields["date"].Should().Be(DateTimeOffset.Parse("2026-03-16T00:00:00+00:00"));
        restored.Fields["true"].Should().Be(true);
        restored.Fields["false"].Should().Be(false);
        restored.Fields["null"].Should().BeNull();
        nullRestored.Should().BeNull();
    }
}

public sealed class ScriptDefinitionSnapshotDocumentJsonConverterTests
{
    [Fact]
    public void DefinitionSnapshot_ShouldRoundTripThroughJson_WithScriptPackageAndRuntimeSemantics()
    {
        var updatedAt = DateTimeOffset.Parse("2026-03-16T16:05:00+00:00");
        var document = new ScriptDefinitionSnapshotDocument
        {
            Id = "definition-1",
            StateVersion = 11,
            LastEventId = "evt-definition",
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
            ScriptId = "script-1",
            DefinitionActorId = "definition-1",
            Revision = "rev-2",
            SourceText = "public sealed class SampleBehavior {}",
            SourceHash = "hash-2",
            StateTypeUrl = "type://state",
            ReadModelTypeUrl = "type://read-model",
            ReadModelSchemaVersion = "2",
            ReadModelSchemaHash = "schema-hash",
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource(
                "public sealed class SampleBehavior {}",
                path: "Behavior.cs",
                entryBehaviorTypeName: "SampleBehavior"),
            ProtocolDescriptorSetBase64 = "AQID",
            StateDescriptorFullName = "sample.State",
            ReadModelDescriptorFullName = "sample.ReadModel",
            RuntimeSemantics = new ScriptRuntimeSemanticsSpec
            {
                Messages =
                {
                    new ScriptMessageSemanticsSpec
                    {
                        TypeUrl = "type://sample.command",
                        DescriptorFullName = "sample.Command",
                        Kind = ScriptMessageKind.Command,
                        Projectable = false,
                        ReplaySafe = true,
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(document);
        var restored = JsonSerializer.Deserialize<ScriptDefinitionSnapshotDocument>(json);

        json.Should().Contain("\"script_package\"");
        json.Should().Contain("\"runtime_semantics\"");
        restored.Should().NotBeNull();
        restored!.ScriptPackage.CsharpSources.Should().ContainSingle();
        restored.ScriptPackage.CsharpSources[0].Path.Should().Be("Behavior.cs");
        restored.ScriptPackage.CsharpSources[0].Content.Should().Contain("SampleBehavior");
        restored.RuntimeSemantics.Messages.Should().ContainSingle();
        restored.RuntimeSemantics.Messages[0].TypeUrl.Should().Be("type://sample.command");
        restored.StateVersion.Should().Be(11);
        restored.LastEventId.Should().Be("evt-definition");
    }
}

public sealed class ScriptCatalogEntryDocumentJsonConverterTests
{
    [Fact]
    public void CatalogEntry_ShouldRoundTripThroughJson_WithRevisionHistory()
    {
        var updatedAt = DateTimeOffset.Parse("2026-03-16T16:05:00+00:00");
        var document = new ScriptCatalogEntryDocument
        {
            Id = "catalog:script-1",
            StateVersion = 5,
            LastEventId = "evt-catalog",
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
            CatalogActorId = "catalog",
            ScriptId = "script-1",
            ActiveRevision = "rev-2",
            ActiveDefinitionActorId = "definition-1",
            ActiveSourceHash = "hash-2",
            PreviousRevision = "rev-1",
            LastProposalId = "proposal-1",
        };
        document.RevisionHistory.Add("rev-1");
        document.RevisionHistory.Add("rev-2");

        var json = JsonSerializer.Serialize(document);
        var restored = JsonSerializer.Deserialize<ScriptCatalogEntryDocument>(json);

        json.Should().Contain("\"revision_history_entries\"");
        restored.Should().NotBeNull();
        restored!.RevisionHistory.Should().Equal("rev-1", "rev-2");
        restored.ActiveRevision.Should().Be("rev-2");
        restored.LastProposalId.Should().Be("proposal-1");
    }
}

public sealed class ScriptEvolutionReadModelJsonConverterTests
{
    [Fact]
    public void EvolutionReadModel_ShouldRoundTripThroughJson_WithDiagnostics()
    {
        var updatedAt = DateTimeOffset.Parse("2026-03-16T16:05:00+00:00");
        var document = new ScriptEvolutionReadModel
        {
            Id = "proposal-1",
            ProposalId = "proposal-1",
            ScriptId = "script-1",
            BaseRevision = "rev-1",
            CandidateRevision = "rev-2",
            ValidationStatus = "validated",
            PromotionStatus = "promoted",
            RollbackStatus = string.Empty,
            FailureReason = string.Empty,
            DefinitionActorId = "definition-1",
            CatalogActorId = "catalog",
            LastEventId = "evt-evolution",
            UpdatedAt = updatedAt,
            StateVersion = 6,
            ActorId = "evolution-session:proposal-1",
        };
        document.Diagnostics.Add("diag-a");
        document.Diagnostics.Add("diag-b");

        var json = JsonSerializer.Serialize(document);
        var restored = JsonSerializer.Deserialize<ScriptEvolutionReadModel>(json);

        json.Should().Contain("\"diagnostics_entries\"");
        restored.Should().NotBeNull();
        restored!.Diagnostics.Should().Equal("diag-a", "diag-b");
        restored.StateVersion.Should().Be(6);
        restored.ActorId.Should().Be("evolution-session:proposal-1");
    }
}
