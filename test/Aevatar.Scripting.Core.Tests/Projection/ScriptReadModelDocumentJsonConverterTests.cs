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
