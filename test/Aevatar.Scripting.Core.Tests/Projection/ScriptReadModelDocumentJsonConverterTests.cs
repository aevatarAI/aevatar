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
