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

    // ─── Additional coverage ───

    [Fact]
    public void JsonRpcResponse_WithNullId_Serializes()
    {
        var response = JsonRpcResponse.Success(null, new { ok = true });
        var json = JsonSerializer.Serialize(response, JsonOptions);

        json.Should().Contain("\"result\"");
        // id should be null in the output
        var deserialized = JsonSerializer.Deserialize<JsonRpcResponse>(json, JsonOptions);
        deserialized!.Id.Should().BeNull();
    }

    [Fact]
    public void JsonRpcResponse_Fail_WithData_IncludesData()
    {
        var response = JsonRpcResponse.Fail(null, A2AErrorCodes.InternalError, "error", new { detail = "stack" });
        var json = JsonSerializer.Serialize(response, JsonOptions);

        json.Should().Contain("\"data\"");
        json.Should().Contain("stack");
    }

    [Fact]
    public void A2AErrorCodes_MatchJsonRpcSpec()
    {
        A2AErrorCodes.ParseError.Should().Be(-32700);
        A2AErrorCodes.InvalidRequest.Should().Be(-32600);
        A2AErrorCodes.MethodNotFound.Should().Be(-32601);
        A2AErrorCodes.InvalidParams.Should().Be(-32602);
        A2AErrorCodes.InternalError.Should().Be(-32603);
    }

    [Fact]
    public void A2ATask_JsonRoundtrip_PreservesAllProperties()
    {
        var task = new A2ATask
        {
            Id = "t-1",
            SessionId = "s-1",
            Status = new A2ATaskStatus
            {
                State = TaskState.Working,
                Timestamp = "2026-04-07T00:00:00Z",
            },
            History = [new Message { Role = "user", Parts = [new TextPart { Text = "hi" }] }],
            Artifacts = [new Artifact { Parts = [new TextPart { Text = "result" }], Index = 0 }],
            Metadata = new() { ["key"] = "value" },
        };

        var json = JsonSerializer.Serialize(task, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<A2ATask>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be("t-1");
        deserialized.SessionId.Should().Be("s-1");
        deserialized.Status.State.Should().Be(TaskState.Working);
        deserialized.History.Should().HaveCount(1);
        deserialized.Artifacts.Should().HaveCount(1);
        deserialized.Metadata!["key"].Should().Be("value");
    }

    [Fact]
    public void A2ATask_WithNullCollections_SerializesCorrectly()
    {
        var task = new A2ATask
        {
            Id = "t-2",
            Status = new A2ATaskStatus { State = TaskState.Submitted },
        };

        var json = JsonSerializer.Serialize(task, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<A2ATask>(json, JsonOptions);

        deserialized!.History.Should().BeNull();
        deserialized.Artifacts.Should().BeNull();
        deserialized.Metadata.Should().BeNull();
    }

    [Fact]
    public void DataPart_Roundtrips()
    {
        Part part = new DataPart
        {
            Data = new Dictionary<string, object?> { ["score"] = 42 },
        };

        var json = JsonSerializer.Serialize(part, JsonOptions);
        json.Should().Contain("\"type\":\"data\"");

        var deserialized = JsonSerializer.Deserialize<Part>(json, JsonOptions);
        deserialized.Should().BeOfType<DataPart>();
    }

    [Fact]
    public void AgentCard_JsonRoundtrip_PreservesAllProperties()
    {
        var card = new AgentCard
        {
            Name = "Agent",
            Description = "Desc",
            Url = "http://localhost/a2a",
            Version = "2.0.0",
            Capabilities = new AgentCapabilities
            {
                Streaming = true,
                PushNotifications = false,
                StateTransitionHistory = true,
            },
            Skills = [new AgentSkill
            {
                Id = "chat",
                Name = "Chat",
                Description = "General chat",
                Tags = ["chat", "ai"],
                Examples = ["Hello"],
            }],
            DefaultInputModes = ["text", "image"],
            DefaultOutputModes = ["text"],
        };

        var json = JsonSerializer.Serialize(card, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AgentCard>(json, JsonOptions);

        deserialized!.Name.Should().Be("Agent");
        deserialized.Version.Should().Be("2.0.0");
        deserialized.Capabilities.Streaming.Should().BeTrue();
        deserialized.Skills.Should().HaveCount(1);
        deserialized.Skills[0].Tags.Should().Contain("ai");
        deserialized.Skills[0].Examples.Should().Contain("Hello");
        deserialized.DefaultInputModes.Should().Contain("image");
    }

    [Fact]
    public void TaskState_AllValues_SerializeAsStrings()
    {
        foreach (var state in Enum.GetValues<TaskState>())
        {
            var status = new A2ATaskStatus { State = state };
            var json = JsonSerializer.Serialize(status, JsonOptions);

            // Should not contain numeric enum values
            json.Should().NotMatchRegex("\"state\":\\d");
        }
    }

    [Fact]
    public void TextPart_WithMetadata_Roundtrips()
    {
        Part part = new TextPart
        {
            Text = "hello",
            Metadata = new() { ["source"] = "user" },
        };

        var json = JsonSerializer.Serialize(part, JsonOptions);
        json.Should().Contain("\"metadata\"");

        var deserialized = JsonSerializer.Deserialize<Part>(json, JsonOptions);
        deserialized.Should().BeOfType<TextPart>();
        deserialized!.Metadata.Should().ContainKey("source");
    }
}
