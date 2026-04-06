using System.Text.Json;
using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Abstractions.Models;
using FluentAssertions;

namespace Aevatar.Interop.A2A.Tests;

public class JsonRpcModelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void JsonRpcRequest_Roundtrips()
    {
        var json = """
        {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "tasks/send",
            "params": { "id": "t1", "message": { "role": "user", "parts": [{"text": "hi"}] } }
        }
        """;

        var request = JsonSerializer.Deserialize<JsonRpcRequest>(json, JsonOptions);
        request.Should().NotBeNull();
        request!.Method.Should().Be("tasks/send");
        request.Id.Should().NotBeNull();
        request.Params.Should().NotBeNull();
    }

    [Fact]
    public void JsonRpcResponse_Success_SerializesCorrectly()
    {
        var response = JsonRpcResponse.Success(
            JsonSerializer.Deserialize<JsonElement>("1"),
            new { status = "ok" });

        var json = JsonSerializer.Serialize(response, JsonOptions);
        json.Should().Contain("result");
        json.Should().NotContain("error");
    }

    [Fact]
    public void JsonRpcResponse_Error_SerializesCorrectly()
    {
        var response = JsonRpcResponse.Fail(null, A2AErrorCodes.MethodNotFound, "Not found");

        var json = JsonSerializer.Serialize(response, JsonOptions);
        json.Should().Contain("error");
        json.Should().Contain("-32601");
        json.Should().NotContain("result");
    }

    [Fact]
    public void TaskState_SerializesAsString()
    {
        var status = new A2ATaskStatus { State = TaskState.Working };
        var json = JsonSerializer.Serialize(status, JsonOptions);
        json.Should().Contain("working");
    }

    [Fact]
    public void AgentCard_Serializes()
    {
        var card = new AgentCard
        {
            Name = "Test",
            Url = "http://localhost/a2a",
        };

        var json = JsonSerializer.Serialize(card, JsonOptions);
        json.Should().Contain("\"name\"");
        json.Should().Contain("\"url\"");
    }

    // ─── Part serialization (A2A spec: {"type":"text","text":"hello"}) ───

    [Fact]
    public void TextPart_Serializes_WithTypeDiscriminator()
    {
        Part part = new TextPart { Text = "hello" };
        var json = JsonSerializer.Serialize(part, JsonOptions);

        json.Should().Contain("\"type\":\"text\"");
        json.Should().Contain("\"text\":\"hello\"");
    }

    [Fact]
    public void TextPart_Deserializes_FromA2AFormat()
    {
        var json = """{"type":"text","text":"hello world"}""";
        var part = JsonSerializer.Deserialize<Part>(json, JsonOptions);

        part.Should().BeOfType<TextPart>();
        ((TextPart)part!).Text.Should().Be("hello world");
    }

    [Fact]
    public void FilePart_Roundtrips()
    {
        Part part = new FilePart { File = new FileContent { Name = "test.txt", MimeType = "text/plain", Uri = "https://example.com/file" } };
        var json = JsonSerializer.Serialize(part, JsonOptions);
        json.Should().Contain("\"type\":\"file\"");

        var deserialized = JsonSerializer.Deserialize<Part>(json, JsonOptions);
        deserialized.Should().BeOfType<FilePart>();
        ((FilePart)deserialized!).File.Name.Should().Be("test.txt");
    }

    [Fact]
    public void Message_WithParts_Roundtrips()
    {
        var message = new Message
        {
            Role = "user",
            Parts = [new TextPart { Text = "Hello" }, new TextPart { Text = "World" }],
        };

        var json = JsonSerializer.Serialize(message, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<Message>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Parts.Should().HaveCount(2);
        deserialized.Parts[0].Should().BeOfType<TextPart>();
        ((TextPart)deserialized.Parts[0]).Text.Should().Be("Hello");
    }

    [Fact]
    public void Part_Deserializes_WithoutTypeField_FallsBackToText()
    {
        // A2A clients might omit "type" for text parts
        var json = """{"text":"implicit text"}""";
        var part = JsonSerializer.Deserialize<Part>(json, JsonOptions);

        part.Should().BeOfType<TextPart>();
        ((TextPart)part!).Text.Should().Be("implicit text");
    }

    [Fact]
    public void TaskSendParams_Deserializes_FromJsonRpc()
    {
        // Simulates what the JSON-RPC endpoint receives
        var json = """
        {
            "id": "task-1",
            "sessionId": "session-1",
            "message": {
                "role": "user",
                "parts": [{"type": "text", "text": "hello agent"}]
            },
            "metadata": {"agentId": "actor-123"}
        }
        """;

        var sendParams = JsonSerializer.Deserialize<TaskSendParams>(json, JsonOptions);
        sendParams.Should().NotBeNull();
        sendParams!.Id.Should().Be("task-1");
        sendParams.Message.Parts.Should().HaveCount(1);
        sendParams.Message.Parts[0].Should().BeOfType<TextPart>();
        ((TextPart)sendParams.Message.Parts[0]).Text.Should().Be("hello agent");
        sendParams.Metadata!["agentId"].Should().Be("actor-123");
    }
}
