using Aevatar.AppPlatform.Hosting.Endpoints;
using Aevatar.AppPlatform.Hosting.OpenApi;
using Aevatar.AppPlatform.Hosting.Serialization;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Aevatar.AppPlatform.Tests;

public class AppPlatformHostingBoundaryTests
{
    [Fact]
    public void Deserialize_ShouldParseTypedJsonPayloadIntoAny()
    {
        var serializer = new AppFunctionInvokeRequestSerializer();
        var request = new AppPlatformEndpointModels.FunctionInvokeHttpRequest(
            CommandId: "cmd-1",
            CorrelationId: "corr-1",
            TypedPayload: new AppPlatformEndpointModels.FunctionInvokeTypedPayloadHttpRequest(
                "type.googleapis.com/google.protobuf.StringValue",
                ParseJsonElement("\"hello world\"")),
            CallerServiceKey: "external-ai",
            CallerTenantId: "scope-dev",
            CallerAppId: "copilot",
            CallerScopeId: "scope-dev",
            CallerSessionId: "session-1");

        var result = serializer.Deserialize(request);

        result.CommandId.Should().Be("cmd-1");
        result.CorrelationId.Should().Be("corr-1");
        result.Payload.TypeUrl.Should().Be("type.googleapis.com/google.protobuf.StringValue");
        result.Payload.Unpack<StringValue>().Value.Should().Be("hello world");
        result.Caller.ServiceKey.Should().Be("external-ai");
        result.Caller.SessionId.Should().Be("session-1");
    }

    [Fact]
    public void Deserialize_WhenBothPayloadShapesAreProvided_ShouldRejectRequest()
    {
        var serializer = new AppFunctionInvokeRequestSerializer();
        var request = new AppPlatformEndpointModels.FunctionInvokeHttpRequest(
            CommandId: "cmd-1",
            CorrelationId: "corr-1",
            BinaryPayload: new AppPlatformEndpointModels.FunctionInvokeBinaryPayloadHttpRequest(
                "type.googleapis.com/google.protobuf.StringValue",
                Convert.ToBase64String([0x01, 0x02, 0x03])),
            TypedPayload: new AppPlatformEndpointModels.FunctionInvokeTypedPayloadHttpRequest(
                "type.googleapis.com/google.protobuf.StringValue",
                ParseJsonElement("\"typed\"")));

        var act = () => serializer.Deserialize(request);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Exactly one of binaryPayload or typedPayload is required.");
    }

    [Fact]
    public void BuildDocument_ShouldExposeTypedAndBinaryInvokeSchemas()
    {
        var port = new AppOpenApiDocumentPort();

        var document = port.BuildDocument("http://localhost:5100");

        var functionInvokeSchema = GetObject(document, "components", "schemas", "FunctionInvokeHttpRequest");
        GetObject(functionInvokeSchema, "properties", "typedPayload")["$ref"]!.GetValue<string>()
            .Should().Be("#/components/schemas/FunctionInvokeTypedPayloadHttpRequest");
        GetObject(functionInvokeSchema, "properties", "binaryPayload")["$ref"]!.GetValue<string>()
            .Should().Be("#/components/schemas/FunctionInvokeBinaryPayloadHttpRequest");
        GetObject(functionInvokeSchema, "properties").ContainsKey("payloadTypeUrl").Should().BeFalse();
        GetObject(functionInvokeSchema, "properties").ContainsKey("payloadBase64").Should().BeFalse();

        var typedPayloadSchema = GetObject(document, "components", "schemas", "FunctionInvokeTypedPayloadHttpRequest");
        GetObject(typedPayloadSchema, "properties", "payloadJson")["description"]!.GetValue<string>()
            .Should().Contain("Protobuf JSON payload");

        var invokeOperation = GetObject(document, "paths", "/api/apps/{appId}/functions/{functionId}:invoke", "post");
        GetObject(invokeOperation, "requestBody", "content", "application/json", "schema")["$ref"]!.GetValue<string>()
            .Should().Be("#/components/schemas/FunctionInvokeHttpRequest");

        var operationResultOperation = GetObject(document, "paths", "/api/operations/{operationId}/result", "get");
        GetObject(operationResultOperation, "responses", "200", "content", "application/json", "schema")["$ref"]!.GetValue<string>()
            .Should().Be("#/components/schemas/AppOperationResult");

        var operationStreamOperation = GetObject(document, "paths", "/api/operations/{operationId}:stream", "get");
        GetObject(operationStreamOperation, "responses", "200", "content", "text/event-stream", "schema")["type"]!.GetValue<string>()
            .Should().Be("string");
    }

    private static JsonObject GetObject(JsonObject root, params string[] path)
    {
        JsonNode? current = root;
        foreach (var segment in path)
        {
            current = current?[segment];
        }

        return current.Should().BeOfType<JsonObject>().Subject;
    }

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
