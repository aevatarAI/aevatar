using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Workflow.Sdk.Contracts;
using FluentAssertions;

namespace Aevatar.Workflow.Sdk.Tests;

public sealed class WorkflowFileInputJsonTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void ShouldSerializeInlineFileSizeBytesWhenProvided()
    {
        var request = new ChatRunRequest
        {
            Prompt = "hello",
            Workflow = "direct",
            ScopeId = "scope-1",
            InputParts =
            [
                new ChatRunContentPart
                {
                    Type = "image",
                    InlineFile = new ChatRunInlineFilePart
                    {
                        DataBase64 = "aGVsbG8=",
                        MediaType = "image/png",
                        Name = "hello.png",
                        SizeBytes = 5,
                    },
                },
            ],
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        using var doc = JsonDocument.Parse(json);

        var inlineFile = doc.RootElement
            .GetProperty("inputParts")[0]
            .GetProperty("inlineFile");
        inlineFile.GetProperty("sizeBytes").GetInt64().Should().Be(5);
        inlineFile.GetProperty("dataBase64").GetString().Should().Be("aGVsbG8=");
    }

    [Fact]
    public void ShouldOmitInlineFileSizeBytesWhenUnset()
    {
        var request = new ChatRunRequest
        {
            Prompt = "hello",
            Workflow = "direct",
            ScopeId = "scope-1",
            InputParts =
            [
                new ChatRunContentPart
                {
                    Type = "image",
                    InlineFile = new ChatRunInlineFilePart
                    {
                        DataBase64 = "aGVsbG8=",
                        MediaType = "image/png",
                    },
                },
            ],
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement
            .GetProperty("inputParts")[0]
            .GetProperty("inlineFile")
            .TryGetProperty("sizeBytes", out _)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void ShouldDeserializeInlineFileSizeBytesWhenProvided()
    {
        var request = JsonSerializer.Deserialize<ChatRunRequest>(
            """
            {
              "prompt": "hello",
              "workflow": "direct",
              "scopeId": "scope-1",
              "inputParts": [
                {
                  "type": "image",
                  "inlineFile": {
                    "dataBase64": "aGVsbG8=",
                    "mediaType": "image/png",
                    "name": "hello.png",
                    "sizeBytes": 5
                  }
                }
              ]
            }
            """,
            JsonOptions);

        request.Should().NotBeNull();
        request!.InputParts.Should().ContainSingle()
            .Which.InlineFile.Should().BeEquivalentTo(new ChatRunInlineFilePart
            {
                DataBase64 = "aGVsbG8=",
                MediaType = "image/png",
                Name = "hello.png",
                SizeBytes = 5,
            });
    }

    [Fact]
    public void ShouldNotExposeSizeBytesOnFileRef()
    {
        typeof(ChatRunFileRefPart)
            .GetProperty("SizeBytes")
            .Should()
            .BeNull();

        var request = new ChatRunRequest
        {
            Prompt = "hello",
            Workflow = "direct",
            ScopeId = "scope-1",
            InputParts =
            [
                new ChatRunContentPart
                {
                    Type = "image",
                    FileRef = new ChatRunFileRefPart
                    {
                        Uri = "artifact://file-1",
                        MediaType = "image/png",
                        Name = "hello.png",
                    },
                },
            ],
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement
            .GetProperty("inputParts")[0]
            .GetProperty("fileRef")
            .TryGetProperty("sizeBytes", out _)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void ShouldDeserializeFileRefPart()
    {
        var request = JsonSerializer.Deserialize<ChatRunRequest>(
            """
            {
              "prompt": "hello",
              "workflow": "direct",
              "scopeId": "scope-1",
              "inputParts": [
                {
                  "type": "image",
                  "fileRef": {
                    "uri": "artifact://file-1",
                    "mediaType": "image/png",
                    "name": "hello.png"
                  }
                }
              ]
            }
            """,
            JsonOptions);

        request.Should().NotBeNull();
        request!.InputParts.Should().ContainSingle()
            .Which.FileRef.Should().BeEquivalentTo(new ChatRunFileRefPart
            {
                Uri = "artifact://file-1",
                MediaType = "image/png",
                Name = "hello.png",
            });
    }
}
