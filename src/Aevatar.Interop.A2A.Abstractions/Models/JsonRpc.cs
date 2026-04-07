using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.Interop.A2A.Abstractions.Models;

/// <summary>A2A JSON-RPC 2.0 request.</summary>
public sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }
}

/// <summary>A2A JSON-RPC 2.0 response.</summary>
public sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; init; }

    public static JsonRpcResponse Success(JsonElement? id, object result) => new()
    {
        Id = id,
        Result = result,
    };

    public static JsonRpcResponse Fail(JsonElement? id, int code, string message, object? data = null) => new()
    {
        Id = id,
        Error = new JsonRpcError { Code = code, Message = message, Data = data },
    };
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public required int Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; init; }
}

/// <summary>A2A JSON-RPC standard error codes.</summary>
public static class A2AErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int TaskNotFound = -32001;
    public const int TaskNotCancelable = -32002;
}
