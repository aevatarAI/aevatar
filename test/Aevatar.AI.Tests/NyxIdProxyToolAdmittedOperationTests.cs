using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Middleware;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;
using System.Text.Json.Nodes;

namespace Aevatar.AI.Tests;

/// <summary>
/// Covers the proof-bound main chain: the committed operation proof owns routing, and every
/// rejection must happen before a single NyxID request leaves the process.
/// </summary>
public sealed class NyxIdProxyToolAdmittedOperationTests
{
    [Fact]
    public void ParametersSchema_ShouldDescribeProofBoundSlotsWithoutRequiringLegacyRouting()
    {
        var tool = CreateTool(new RecordingHandler());

        using var document = JsonDocument.Parse(tool.ParametersSchema);
        var root = document.RootElement;
        var properties = root.GetProperty("properties");
        properties.TryGetProperty("path_params", out _).Should().BeTrue();
        properties.TryGetProperty("query", out _).Should().BeTrue();
        properties.TryGetProperty("headers", out _).Should().BeTrue();
        properties.TryGetProperty("body", out _).Should().BeTrue();
        properties.TryGetProperty("response_mode", out _).Should().BeTrue();
        root.TryGetProperty("required", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("text")]
    [InlineData("file_artifact")]
    public async Task ExecuteAsync_ShouldRejectProoflessManagedWorkflowBeforeDownstreamWork(
        string responseMode)
    {
        var handler = new RecordingHandler(binaryResponse: responseMode == "file_artifact");
        var ingress = new RecordingFileArtifactIngress();
        var tool = CreateTool(
            handler,
            ingress,
            NyxIdManagedWorkflowAdmissionMode.Enforce);
        using var scope = PushProoflessManagedContext();
        var arguments = JsonSerializer.Serialize(new
        {
            service_id = "us-service-alpha",
            slug = "calendar-alpha",
            path = "/events/evt-alpha",
            response_mode = responseMode,
        });

        var result = await tool.ExecuteAsync(arguments);

        result.Should().Contain("NYXID_OPERATION_ADMISSION_REQUIRED");
        handler.RequestCount.Should().Be(0);
        ingress.Requests.Should().BeEmpty();
        tool.CreateResultReceipt("call-alpha", "nyxid_proxy", arguments, result)!
            .ErrorCode.Should().Be("NYXID_OPERATION_ADMISSION_REQUIRED");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepProoflessManagedWorkflowOnLegacyPathInShadowMode()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(
            handler,
            managedWorkflowAdmissionMode: NyxIdManagedWorkflowAdmissionMode.Shadow);
        using var scope = PushProoflessManagedContext();

        var result = await tool.ExecuteAsync(
            """{"service_id":"us-service-alpha","slug":"calendar-alpha","path":"/events/evt-alpha"}""");

        result.Should().NotContain("NYXID_OPERATION_ADMISSION_REQUIRED");
        handler.ProxyRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepOrdinaryHumanRawProxyPathInEnforceMode()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(
            handler,
            managedWorkflowAdmissionMode: NyxIdManagedWorkflowAdmissionMode.Enforce);
        using var scope = PushHumanContext();

        var result = await tool.ExecuteAsync(
            """{"service_id":"us-service-alpha","slug":"calendar-alpha","path":"/events/evt-alpha"}""");

        result.Should().NotContain("NYXID_OPERATION_ADMISSION_REQUIRED");
        handler.ProxyRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ToolApprovalMiddleware_ShouldKeepOrdinaryHumanRawProxyOnNyxIdOwnedApprovalPath()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(
            handler,
            managedWorkflowAdmissionMode: NyxIdManagedWorkflowAdmissionMode.Enforce);
        var approvalHandler = new RecordingApprovalHandler();
        using var scope = PushHumanContext();
        const string arguments =
            """{"service_id":"us-service-alpha","slug":"calendar-alpha","path":"/events/evt-alpha"}""";
        var context = new ToolCallContext
        {
            Tool = tool,
            ToolName = tool.Name,
            ToolCallId = "call-human-alpha",
            ArgumentsJson = arguments,
        };

        var nextExecuted = false;
        await new ToolApprovalMiddleware(approvalHandler).InvokeAsync(context, async () =>
        {
            nextExecuted = true;
            context.Result = await tool.ExecuteAsync(arguments);
        });

        nextExecuted.Should().BeTrue();
        approvalHandler.Requests.Should().BeEmpty();
        handler.ProxyRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRecordOnlyBoundedProoflessManagedDecisionTelemetry()
    {
        var measurements = new List<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Aevatar.AI.ToolProviders.NyxId" &&
                instrument.Name == "aevatar.nyxid.proxy.admission.decisions")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            measurements.Add(tags.ToArray()));
        listener.Start();
        var handler = new RecordingHandler();
        var tool = CreateTool(
            handler,
            managedWorkflowAdmissionMode: NyxIdManagedWorkflowAdmissionMode.Enforce);
        using var scope = PushProoflessManagedContext();

        await tool.ExecuteAsync(
            """{"service_id":"us-secret-alpha","slug":"secret-alpha","path":"/secret/alpha","body":"secret-body"}""");

        var tags = measurements.Should().ContainSingle().Subject.ToDictionary();
        tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["aevatar.nyxid.admission.mode"] = "enforce",
            ["aevatar.nyxid.admission.managed"] = true,
            ["aevatar.nyxid.admission.proof_present"] = false,
            ["aevatar.nyxid.admission.invocation_surface"] = "workflow_llm_tool_loop",
            ["aevatar.nyxid.admission.risk"] = "unspecified",
            ["aevatar.nyxid.admission.would_approve"] = false,
            ["aevatar.nyxid.admission.would_block"] = true,
        });
        tags.Values.Select(static value => value?.ToString()).Should().NotContain(value =>
            value != null && value.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldBuildTheConcretePathFromTheProofTemplate()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(MessageResourceAdmission());

        var result = await tool.ExecuteAsync(
            """{"path_params":{"message_id":"om_x1","file_key":"file 7"}}""");

        result.Should().NotContain("error_code");
        var request = handler.ProxyRequests.Should().ContainSingle().Subject;
        request.Path.Should().Be("/api/v1/proxy/s/api-lark-bot-2/open-apis/im/v1/messages/om_x1/resources/file%207");
        request.Query.Should().Be("?_nyxid_via=us-lark-alpha");
        request.Method.Should().Be("GET");
    }

    [Theory]
    [InlineData("../../../api-keys")]
    [InlineData("/%2e%2e/%2e%2e/api-keys")]
    public async Task ExecuteAsync_ShouldRejectUnsafeStaticProofPathBeforeAnyHttpRequest(string pathTemplate)
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(ListMessagesAdmission() with
        {
            PathTemplate = pathTemplate,
            Parameters = [],
        });

        var result = await tool.ExecuteAsync("{}");

        result.Should().Contain("NYXID_OPERATION_PATH_TEMPLATE_INVALID");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldAcceptDynamicMessageResourceAsAFileArtifact()
    {
        var handler = new RecordingHandler(binaryResponse: true);
        var ingress = new RecordingFileArtifactIngress();
        var tool = CreateTool(handler, ingress);
        using var scope = PushContext(MessageResourceAdmission());

        var result = await tool.ExecuteAsync(
            """{"path_params":{"message_id":"om_runtime_7","file_key":"file_runtime_9"},"response_mode":"file_artifact"}""");

        using var document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        handler.ProxyRequests.Should().ContainSingle().Which.Path.Should().Be(
            "/api/v1/proxy/s/api-lark-bot-2/open-apis/im/v1/messages/om_runtime_7/resources/file_runtime_9");
        ingress.Requests.Should().ContainSingle().Which.SourceResourceKey.Should().Be(
            "/open-apis/im/v1/messages/om_runtime_7/resources/file_runtime_9");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectNonGetFileArtifactProofBeforeAnyHttpRequest()
    {
        var handler = new RecordingHandler(binaryResponse: true);
        var ingress = new RecordingFileArtifactIngress();
        var tool = CreateTool(handler, ingress);
        using var scope = PushContext(MessageResourceAdmission() with { HttpMethod = "POST" });

        var result = await tool.ExecuteAsync(
            """{"path_params":{"message_id":"om_runtime_7","file_key":"file_runtime_9"},"response_mode":"file_artifact"}""");

        result.Should().Contain("NYXID_OPERATION_RESPONSE_MODE_REJECTED");
        handler.RequestCount.Should().Be(0);
        ingress.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepQueryOutOfThePathAndOffTheWire_ForProofIdentity()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(ListMessagesAdmission());

        await tool.ExecuteAsync(
            """{"query":{"container_id":"oc_1","page_size":"50"}}""");

        var request = handler.ProxyRequests.Should().ContainSingle().Subject;
        request.Path.Should().Be("/api/v1/proxy/s/api-lark-bot-2/open-apis/im/v1/messages");
        request.Query.Should().Be("?_nyxid_via=us-lark-alpha&container_id=oc_1&page_size=50");
        handler.RequestBodies.Should().OnlyContain(body => !body.Contains("lark_list_messages"));
        handler.RequestUris.Should().OnlyContain(uri =>
            !uri.Contains("operation_id") && !uri.Contains("contract_digest"));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectLiveRouteDriftBeforeProxyDispatch()
    {
        var handler = new RecordingHandler
        {
            AuthorityJson = LiveAuthorityJson(
                "us-lark-alpha",
                "api-lark-bot-2",
                nodeId: "node-beta"),
        };
        var tool = CreateTool(handler);
        using var scope = PushContext(ListMessagesAdmission() with
        {
            ServiceAuthority = LiveAuthority("us-lark-alpha", nodeId: "node-alpha"),
        });

        var result = await tool.ExecuteAsync(
            """{"query":{"container_id":"oc_1"}}""");

        result.Should().Contain("NYXID_OPERATION_AUTHORITY_DRIFT");
        handler.AuthorityRequests.Should().ContainSingle();
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectLiveContractDigestDriftBeforeProxyDispatch()
    {
        var handler = new RecordingHandler
        {
            AuthorityJson = LiveAuthorityJson("us-lark-alpha", "api-lark-bot-2"),
            OpenApiJson = """
                {
                  "openapi": "3.1.0",
                  "paths": {
                    "/open-apis/im/v1/messages": {
                      "post": {
                        "operationId": "lark_list_messages",
                        "x-aevatar-tool": { "enabled": true }
                      }
                    }
                  }
                }
                """,
        };
        var tool = CreateTool(handler);
        using var scope = PushContext(ListMessagesAdmission() with
        {
            ServiceAuthority = LiveAuthority("us-lark-alpha"),
        });

        var result = await tool.ExecuteAsync(
            """{"query":{"container_id":"oc_1"}}""");

        result.Should().Contain("NYXID_OPERATION_CONTRACT_DRIFT");
        handler.AuthorityRequests.Should().ContainSingle();
        handler.OpenApiRequests.Should().ContainSingle();
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("_nyxid_via")]
    [InlineData("_NYXID_ROUTE")]
    public async Task ExecuteAsync_ShouldRejectReservedProofQueryNamesBeforeAnyHttpRequest(string name)
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        var admission = ListMessagesAdmission() with
        {
            Parameters = [QueryParameter(name, required: false)],
        };
        using var scope = PushContext(admission);
        var result = await tool.ExecuteAsync("{}");

        result.Should().Contain("NYXID_OPERATION_QUERY_PARAMETER_FORBIDDEN");
        handler.RequestCount.Should().Be(0);
    }

    [Theory]
    [InlineData("""{"path_params":{"message_id":"om_1"}}""", "NYXID_OPERATION_PATH_PARAMETER_MISSING")]
    [InlineData("""{"path_params":{"message_id":"om_1","file_key":"f1","extra":"x"}}""", "NYXID_OPERATION_PATH_PARAMETER_UNKNOWN")]
    [InlineData("""{"path_params":{"message_id":"a/b","file_key":"f1"}}""", "NYXID_OPERATION_PATH_PARAMETER_INVALID")]
    [InlineData("""{"path_params":{"message_id":"a%2Fb","file_key":"f1"}}""", "NYXID_OPERATION_PATH_PARAMETER_INVALID")]
    [InlineData("""{"path_params":{"message_id":"..","file_key":"f1"}}""", "NYXID_OPERATION_PATH_PARAMETER_INVALID")]
    [InlineData("""{"path_params":{"message_id":"%2e%2e","file_key":"f1"}}""", "NYXID_OPERATION_PATH_PARAMETER_INVALID")]
    [InlineData("""{"path_params":{"message_id":"${input}","file_key":"f1"}}""", "NYXID_OPERATION_PATH_PARAMETER_INVALID")]
    [InlineData("""{"path_params":{"message_id":"om_1","file_key":"f1"},"query":{"nope":"1"}}""", "NYXID_OPERATION_QUERY_PARAMETER_UNKNOWN")]
    [InlineData("""{"path_params":{"message_id":"om_1","file_key":"f1"},"headers":{"X-Trace":"1"}}""", "NYXID_OPERATION_HEADER_NOT_DECLARED")]
    [InlineData("""{"path_params":{"message_id":"om_1","file_key":"f1"},"body":{"a":1}}""", "NYXID_OPERATION_BODY_NOT_SUPPORTED")]
    [InlineData("""{"path":"/anything"}""", "NYXID_OPERATION_ARGUMENT_NOT_SUPPORTED")]
    [InlineData("""{"method":"DELETE"}""", "NYXID_OPERATION_ARGUMENT_NOT_SUPPORTED")]
    [InlineData("""{"contract_digest":"forged"}""", "NYXID_OPERATION_ARGUMENT_NOT_SUPPORTED")]
    public async Task ExecuteAsync_ShouldFailClosedWithoutAnyHttpRequest(
        string argumentsJson,
        string expectedCode)
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(MessageResourceAdmission());

        var result = await tool.ExecuteAsync(argumentsJson);

        result.Should().Contain(expectedCode);
        handler.RequestCount.Should().Be(0);
    }

    [Theory]
    [InlineData('\\')]
    [InlineData('\0')]
    public async Task ExecuteAsync_ShouldRejectTraversalControlCharacters_BeforeAnyHttpRequest(char hostile)
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(MessageResourceAdmission());
        var arguments = JsonSerializer.Serialize(new
        {
            path_params = new Dictionary<string, string>
            {
                ["message_id"] = $"a{hostile}b",
                ["file_key"] = "f1",
            },
        });

        var result = await tool.ExecuteAsync(arguments);

        // A NUL cannot even survive JSON reading, so the two hostile characters fail closed at
        // different boundaries; both must produce a typed code and leave the wire untouched.
        result.Should().Contain("\"error\":true").And.Contain("NYXID_OPERATION_");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectFileArtifact_WhenTheProofPublishesNoBinaryResponse()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(ListMessagesAdmission());

        var result = await tool.ExecuteAsync("""{"response_mode":"file_artifact"}""");

        result.Should().Contain("NYXID_OPERATION_RESPONSE_MODE_REJECTED");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldValidateTheBodyAgainstTheAdmittedSchema()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(CreateApprovalAdmission());

        var rejected = await tool.ExecuteAsync(
            """{"body":{"approval_code":123}}""");
        rejected.Should().Contain("NYXID_OPERATION_BODY_INVALID");
        handler.RequestCount.Should().Be(0);

        var missingRequired = await tool.ExecuteAsync("""{"body":{"form":"{}"}}""");
        missingRequired.Should().Contain("NYXID_OPERATION_BODY_INVALID");
        handler.RequestCount.Should().Be(0);

        var accepted = await tool.ExecuteAsync(
            """{"body":{"approval_code":"AC-1","form":"{}"}}""");
        accepted.Should().NotContain("NYXID_OPERATION_BODY_INVALID");
        var request = handler.ProxyRequests.Should().ContainSingle().Subject;
        request.Method.Should().Be("POST");
        request.Body.Should().Contain("AC-1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRequireAdmittedHeadersBeforeAnyHttpRequest()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(TypedParametersAdmission());

        var result = await tool.ExecuteAsync(
            """{"path_params":{"item_id":7},"query":{"ratio":1.5}}""");

        result.Should().Contain("NYXID_OPERATION_HEADER_MISSING");
        handler.RequestCount.Should().Be(0);
    }

    [Theory]
    [InlineData("""{"path_params":{"item_id":"7"},"query":{"ratio":1.5},"headers":{"If-Match":true}}""", "NYXID_OPERATION_PATH_PARAMETER_INVALID")]
    [InlineData("""{"path_params":{"item_id":7},"query":{"ratio":2.5},"headers":{"If-Match":true}}""", "NYXID_OPERATION_QUERY_PARAMETER_INVALID")]
    [InlineData("""{"path_params":{"item_id":7},"query":{"ratio":1.5,"mode":"brief"},"headers":{"If-Match":true}}""", "NYXID_OPERATION_QUERY_PARAMETER_INVALID")]
    [InlineData("""{"path_params":{"item_id":7},"query":{"ratio":1.5},"headers":{"If-Match":"true"}}""", "NYXID_OPERATION_HEADER_INVALID")]
    public async Task ExecuteAsync_ShouldValidateNonBodyValuesAgainstAdmittedSchemas(
        string argumentsJson,
        string expectedCode)
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(TypedParametersAdmission());

        var result = await tool.ExecuteAsync(argumentsJson);

        result.Should().Contain(expectedCode);
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldAcceptTypedNonBodyValuesThatMatchTheProof()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(TypedParametersAdmission());

        var result = await tool.ExecuteAsync(
            """{"path_params":{"item_id":7},"query":{"ratio":1.5,"mode":"full"},"headers":{"If-Match":true}}""");

        result.Should().NotContain("error_code");
        var request = handler.ProxyRequests.Should().ContainSingle().Subject;
        request.Path.Should().EndWith("/items/7");
        request.Query.Should().Contain("ratio=1.5");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldBuildDynamicApprovalInstancePathFromTheProof()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(GetApprovalInstanceAdmission());

        var result = await tool.ExecuteAsync(
            """{"path_params":{"instance_code":"approval_runtime_42"}}""");

        result.Should().NotContain("error_code");
        var request = handler.ProxyRequests.Should().ContainSingle().Subject;
        request.Method.Should().Be("GET");
        request.Path.Should().Be(
            "/api/v1/proxy/s/api-lark-bot-2/open-apis/approval/v4/instances/approval_runtime_42");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldUseTheSameBuilderForEveryIterationOfAnIndirectCallSite()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(MessageResourceAdmission());

        foreach (var messageId in (string[])["om_1", "om_2", "om_3"])
        {
            await tool.ExecuteAsync(
                $$$"""{"path_params":{"message_id":"{{{messageId}}}","file_key":"f"}}""");
        }

        handler.ProxyRequests.Select(static request => request.Path).Should().Equal(
            "/api/v1/proxy/s/api-lark-bot-2/open-apis/im/v1/messages/om_1/resources/f",
            "/api/v1/proxy/s/api-lark-bot-2/open-apis/im/v1/messages/om_2/resources/f",
            "/api/v1/proxy/s/api-lark-bot-2/open-apis/im/v1/messages/om_3/resources/f");
    }

    [Fact]
    public void GetCallSafety_ShouldUseTheTypedProofPolicy()
    {
        var tool = CreateTool(new RecordingHandler());

        using (PushContext(MessageResourceAdmission()))
        {
            tool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);
            tool.GetCallSafety("{}").Should().Be(new AgentToolCallSafety(
                RequiresApproval: false,
                IsReadOnly: true,
                IsDestructive: false));
        }

        using (PushContext(CreateApprovalAdmission()))
        {
            tool.GetCallSafety("{}").Should().Be(new AgentToolCallSafety(
                RequiresApproval: true,
                IsReadOnly: false,
                IsDestructive: false));
        }
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectManagedProofWithoutTypedPolicyInEnforceMode()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(
            handler,
            managedWorkflowAdmissionMode: NyxIdManagedWorkflowAdmissionMode.Enforce);
        using var scope = PushContext(MessageResourceAdmission() with
        {
            ExecutionPolicy = AgentToolOperationExecutionPolicy.Unspecified,
        });

        var result = await tool.ExecuteAsync(
            """{"path_params":{"message_id":"om-alpha","file_key":"file-alpha"}}""");

        result.Should().Contain("NYXID_OPERATION_ADMISSION_REQUIRED");
        handler.RequestCount.Should().Be(0);
    }

    private static AgentToolOperationAdmission MessageResourceAdmission() =>
        new(
            "us-lark-alpha",
            "api-lark-bot-2",
            "lark_get_message_resource",
            "GET",
            "/open-apis/im/v1/messages/{message_id}/resources/{file_key}",
            "sha256:message-resource",
            [
                PathParameter("message_id"),
                PathParameter("file_key"),
            ],
            null,
            new AgentToolOperationResponsePolicy(false, true, ["application/octet-stream"]),
            ReadOnlyPolicy());

    private static AgentToolOperationAdmission ListMessagesAdmission() =>
        new(
            "us-lark-alpha",
            "api-lark-bot-2",
            "lark_list_messages",
            "GET",
            "/open-apis/im/v1/messages",
            "sha256:list-messages",
            [
                QueryParameter("container_id", required: true),
                QueryParameter("page_size", required: false),
            ],
            null,
            AgentToolOperationResponsePolicy.TextOnly,
            ReadOnlyPolicy());

    private static AgentToolOperationAdmission CreateApprovalAdmission() =>
        new(
            "us-lark-alpha",
            "api-lark-bot-2",
            "lark_create_approval_instance",
            "POST",
            "/open-apis/approval/v4/instances",
            "sha256:create-approval",
            [],
            new AgentToolOperationRequestBody(
                true,
                "application/json",
                new AgentToolOperationValueSchema(
                    AgentToolOperationValueKind.Object,
                    [
                        new AgentToolOperationSchemaProperty("approval_code", AgentToolOperationValueSchema.Text),
                        new AgentToolOperationSchemaProperty("form", AgentToolOperationValueSchema.Text),
                    ],
                    new HashSet<string>(StringComparer.Ordinal) { "approval_code" },
                    null,
                    [],
                    false)),
            AgentToolOperationResponsePolicy.TextOnly,
            WritePolicy());

    private static AgentToolOperationAdmission GetApprovalInstanceAdmission() =>
        new(
            "us-lark-alpha",
            "api-lark-bot-2",
            "lark_get_approval_instance",
            "GET",
            "/open-apis/approval/v4/instances/{instance_code}",
            "sha256:get-approval-instance",
            [PathParameter("instance_code")],
            null,
            AgentToolOperationResponsePolicy.TextOnly,
            ReadOnlyPolicy());

    private static AgentToolOperationAdmission TypedParametersAdmission() =>
        new(
            "us-items-alpha",
            "items-alpha",
            "get_item",
            "GET",
            "/items/{item_id}",
            "sha256:get-item",
            [
                new AgentToolOperationParameter(
                    "item_id",
                    AgentToolOperationParameterLocation.Path,
                    true,
                    ValueSchema(AgentToolOperationValueKind.Integer)),
                new AgentToolOperationParameter(
                    "ratio",
                    AgentToolOperationParameterLocation.Query,
                    true,
                    ValueSchema(AgentToolOperationValueKind.Number, "1.5")),
                new AgentToolOperationParameter(
                    "mode",
                    AgentToolOperationParameterLocation.Query,
                    false,
                    ValueSchema(AgentToolOperationValueKind.String, "full")),
                new AgentToolOperationParameter(
                    "If-Match",
                    AgentToolOperationParameterLocation.Header,
                    true,
                    ValueSchema(AgentToolOperationValueKind.Boolean)),
            ],
            null,
            AgentToolOperationResponsePolicy.TextOnly,
            ReadOnlyPolicy());

    private static AgentToolOperationValueSchema ValueSchema(
        AgentToolOperationValueKind kind,
        params string[] allowedValues) =>
        new(
            kind,
            [],
            new HashSet<string>(StringComparer.Ordinal),
            null,
            allowedValues,
            false);

    private static AgentToolOperationExecutionPolicy ReadOnlyPolicy() =>
        new(
            AgentToolOperationRisk.ReadOnly,
            AgentToolOperationApproval.None,
            AgentToolOperationEnforcementOwner.Aevatar,
            [AgentToolOperationExecutionMode.Interactive, AgentToolOperationExecutionMode.Durable]);

    private static AgentToolOperationExecutionPolicy WritePolicy() =>
        new(
            AgentToolOperationRisk.Write,
            AgentToolOperationApproval.Required,
            AgentToolOperationEnforcementOwner.Aevatar,
            [AgentToolOperationExecutionMode.Interactive]);

    private static AgentToolOperationParameter PathParameter(string name) =>
        new(name, AgentToolOperationParameterLocation.Path, true, AgentToolOperationValueSchema.Text);

    private static AgentToolOperationParameter QueryParameter(string name, bool required) =>
        new(name, AgentToolOperationParameterLocation.Query, required, AgentToolOperationValueSchema.Text);

    private static AgentToolServiceAuthoritySnapshot LiveAuthority(
        string serviceId,
        string nodeId = "node-alpha") =>
        new(
            "https://service.internal",
            "endpoint-alpha",
            serviceId,
            new AgentToolServiceRouteAuthority(
                AgentToolServiceRouteKind.CatalogService,
                "catalog-alpha"),
            nodeId,
            AgentToolServiceCredentialSource.Personal);

    private static string LiveAuthorityJson(
        string serviceId,
        string slug,
        string nodeId = "node-alpha") =>
        JsonSerializer.Serialize(new
        {
            id = serviceId,
            slug,
            endpoint_url = "https://service.internal",
            endpoint_id = "endpoint-alpha",
            catalog_service_id = "catalog-alpha",
            node_id = nodeId,
            openapi_url = $"https://nyx.test/api/v1/proxy/services/{serviceId}/openapi.json",
            is_active = true,
            credential_source = new { type = "personal" },
        });

    private static NyxIdProxyTool CreateTool(
        RecordingHandler handler,
        INyxIdProxyFileArtifactIngress? ingress = null,
        NyxIdManagedWorkflowAdmissionMode managedWorkflowAdmissionMode =
            NyxIdManagedWorkflowAdmissionMode.Shadow) =>
        new(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(handler)),
            fileArtifactIngress: ingress,
            managedWorkflowAdmissionMode: managedWorkflowAdmissionMode);

    private static AgentToolContextScope PushContext(AgentToolOperationAdmission admission) =>
        AgentToolContextScope.Push(new AgentToolExecutionContext(
            AgentToolRequestIdentity.Empty,
            new AgentToolCredentials("user-token", null, null),
            new AgentToolCallerContext("scope-alpha", null, null),
            AgentToolChannelContext.Empty,
            AgentToolSenderBindingContext.Empty,
            LLMRequestRoutingContext.Empty,
            AgentToolConnectedServicesContext.Empty,
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal))
        {
            OperationAdmission = WithLiveProof(admission),
            WorkflowRuntime = new AgentWorkflowRuntimeContext(
                "workflow-run-actor-alpha",
                "run-alpha",
                "step-alpha",
                "run-alpha",
                1),
            InvocationSurface = AgentToolInvocationSurface.WorkflowToolCall,
        });

    private static AgentToolOperationAdmission WithLiveProof(AgentToolOperationAdmission admission)
    {
        var spec = BuildLiveOpenApi(admission);
        var operation = OpenApiToolSpecParser.Parse(spec).AdmittedOperations().Single();
        return admission with
        {
            ContractDigest = ExternalWorkflowCapabilityContractDigest.Compute(
                ["nyxid-openapi-operation.v1", operation.OperationId, operation.CanonicalContract()]),
            ServiceAuthority = admission.ServiceAuthority ?? LiveAuthority(admission.ServiceInstanceId),
        };
    }

    private static string BuildLiveOpenApi(AgentToolOperationAdmission admission)
    {
        var operation = new JsonObject
        {
            ["operationId"] = admission.OperationId,
            ["x-aevatar-tool"] = new JsonObject
            {
                ["enabled"] = true,
                ["readOnly"] = admission.ExecutionPolicy.Risk == AgentToolOperationRisk.ReadOnly,
                ["destructive"] = admission.ExecutionPolicy.Risk == AgentToolOperationRisk.Destructive,
            },
        };
        if (admission.Parameters.Count > 0)
        {
            operation["parameters"] = new JsonArray(admission.Parameters.Select(parameter =>
                (JsonNode)new JsonObject
                {
                    ["name"] = parameter.Name,
                    ["in"] = parameter.Location.ToString().ToLowerInvariant(),
                    ["required"] = parameter.Required,
                    ["schema"] = ToOpenApiSchema(parameter.Schema),
                }).ToArray());
        }
        if (admission.RequestBody is { } body)
        {
            operation["requestBody"] = new JsonObject
            {
                ["required"] = body.Required,
                ["content"] = new JsonObject
                {
                    [body.MediaType] = new JsonObject { ["schema"] = ToOpenApiSchema(body.Schema) },
                },
            };
        }

        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["paths"] = new JsonObject
            {
                [admission.PathTemplate] = new JsonObject
                {
                    [admission.HttpMethod.ToLowerInvariant()] = operation,
                },
            },
        }.ToJsonString();
    }

    private static JsonObject ToOpenApiSchema(AgentToolOperationValueSchema schema)
    {
        var result = new JsonObject
        {
            ["type"] = schema.Kind.ToString().ToLowerInvariant(),
        };
        if (schema.AllowedValues.Count > 0)
        {
            result["enum"] = new JsonArray(schema.AllowedValues.Select(value =>
                schema.Kind switch
                {
                    AgentToolOperationValueKind.Integer => JsonValue.Create(long.Parse(value)),
                    AgentToolOperationValueKind.Number => JsonValue.Create(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
                    AgentToolOperationValueKind.Boolean => JsonValue.Create(bool.Parse(value)),
                    _ => JsonValue.Create(value),
                }).ToArray<JsonNode?>());
        }
        if (schema.Kind == AgentToolOperationValueKind.Object)
        {
            result["additionalProperties"] = schema.AdditionalPropertiesAllowed;
            result["properties"] = new JsonObject(schema.Properties.Select(property =>
                new KeyValuePair<string, JsonNode?>(property.Name, ToOpenApiSchema(property.Schema))));
            if (schema.RequiredProperties.Count > 0)
                result["required"] = new JsonArray(schema.RequiredProperties
                    .Select(static value => (JsonNode?)JsonValue.Create(value))
                    .ToArray());
        }
        else if (schema.Kind == AgentToolOperationValueKind.Array && schema.Items is not null)
        {
            result["items"] = ToOpenApiSchema(schema.Items);
        }
        return result;
    }

    private static AgentToolContextScope PushProoflessManagedContext() =>
        AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-alpha", "call-alpha"),
            Credentials = new AgentToolCredentials("user-token", null, null),
            Caller = new AgentToolCallerContext("scope-alpha", null, null),
            WorkflowRuntime = new AgentWorkflowRuntimeContext(
                "workflow-run-actor-alpha",
                "run-alpha",
                "llm-alpha",
                "run-alpha",
                1),
            InvocationSurface = AgentToolInvocationSurface.WorkflowLlmToolLoop,
        });

    private static AgentToolContextScope PushHumanContext() =>
        AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity("request-human-alpha", "call-human-alpha"),
            Credentials = new AgentToolCredentials("user-token", null, null),
            Caller = new AgentToolCallerContext("scope-human-alpha", null, null),
            InvocationSurface = AgentToolInvocationSurface.HumanSession,
        });

    private sealed record RecordedProxyRequest(string Method, string Path, string Query, string Body);

    private sealed class RecordingHandler(bool binaryResponse = false) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public List<RecordedProxyRequest> ProxyRequests { get; } = [];

        public List<string> RequestBodies { get; } = [];

        public List<string> RequestUris { get; } = [];

        public List<string> AuthorityRequests { get; } = [];

        public List<string> OpenApiRequests { get; } = [];

        public string? AuthorityJson { get; init; }

        public string? OpenApiJson { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            RequestUris.Add(request.RequestUri!.ToString());
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api/v1/keys/", StringComparison.Ordinal))
                AuthorityRequests.Add(path);
            else if (path.StartsWith("/api/v1/proxy/services/", StringComparison.Ordinal))
                OpenApiRequests.Add(path);
            else if (path.StartsWith("/api/v1/proxy/s/", StringComparison.Ordinal))
            {
                ProxyRequests.Add(new RecordedProxyRequest(
                    request.Method.Method,
                    request.RequestUri.AbsolutePath,
                    request.RequestUri.Query,
                    body));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = AuthorityRequests.Count > 0 && AuthorityRequests[^1] == path
                    ? new StringContent(
                        AuthorityJson ?? LiveAuthorityJson(
                            AgentToolRequestContext.Current!.OperationAdmission!.ServiceInstanceId,
                            AgentToolRequestContext.Current.OperationAdmission.ServiceSlug),
                        Encoding.UTF8,
                        "application/json")
                    : OpenApiRequests.Count > 0 && OpenApiRequests[^1] == path
                        ? new StringContent(
                            OpenApiJson ?? BuildLiveOpenApi(AgentToolRequestContext.Current!.OperationAdmission!),
                            Encoding.UTF8,
                            "application/json")
                        : binaryResponse
                    ? new ByteArrayContent(Encoding.UTF8.GetBytes("fixture-resource"))
                    : new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            if (binaryResponse)
                response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
            return response;
        }
    }

    private sealed class RecordingFileArtifactIngress : INyxIdProxyFileArtifactIngress
    {
        public List<FileArtifactIngressRequest> Requests { get; } = [];

        public ValueTask<FileArtifactIngressResult> IngestAsync(
            FileArtifactIngressRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new FileArtifactIngressResult(new FileArtifactRef
            {
                FileId = "file-fixture",
                ArtifactId = "artifact-fixture",
                SourceKind = request.SourceKind,
                SourceMessageId = request.SourceMessageId,
                SourceResourceKey = request.SourceResourceKey,
                FileName = request.FileName,
                MediaType = request.MediaType,
                SizeBytes = request.Content.Length,
                Sha256 = "fixture-sha",
                OwnerRunId = request.OwnerRunId,
                OwnerScopeId = request.OwnerScopeId,
            }));
        }
    }

    private sealed class RecordingApprovalHandler : IToolApprovalHandler
    {
        public List<ToolApprovalRequest> Requests { get; } = [];

        public Task<ToolApprovalResult> RequestApprovalAsync(
            ToolApprovalRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(ToolApprovalResult.Denied("unexpected local approval"));
        }
    }
}
