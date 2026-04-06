using System.Text.Json;
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
}
