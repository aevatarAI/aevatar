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
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.AI.Tests;

/// <summary>
/// Covers the proof-bound main chain: the committed operation proof owns routing, and every
/// rejection must happen before a single NyxID request leaves the process.
/// </summary>
public sealed class NyxIdProxyToolAdmittedOperationTests
{
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

    [Theory]
    [InlineData("""{"query":{"container_id":"blocked","page_size":50}}""")]
    [InlineData("""{"query":{"container_id":"chat","page_size":"50"}}""")]
    public async Task ExecuteAsync_ShouldEnforceQueryParameterSchemasBeforeAnyHttpRequest(
        string argumentsJson)
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(ListMessagesAdmission() with
        {
            Parameters =
            [
                QueryParameter("container_id", required: true, TextSchema("chat")),
                QueryParameter("page_size", required: false, ScalarSchema(AgentToolOperationValueKind.Integer)),
            ],
        });

        var result = await tool.ExecuteAsync(argumentsJson);

        result.Should().Contain("NYXID_OPERATION_QUERY_PARAMETER_INVALID");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldEnforcePathParameterSchemasBeforeAnyHttpRequest()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(GetApprovalInstanceAdmission() with
        {
            Parameters =
            [
                new AgentToolOperationParameter(
                    "instance_code",
                    AgentToolOperationParameterLocation.Path,
                    true,
                    ScalarSchema(AgentToolOperationValueKind.Integer)),
            ],
        });

        var result = await tool.ExecuteAsync(
            """{"path_params":{"instance_code":"approval_runtime_42"}}""");

        result.Should().Contain("NYXID_OPERATION_PATH_PARAMETER_INVALID");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRequireAdmittedHeadersBeforeAnyHttpRequest()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(ListMessagesAdmission() with
        {
            Parameters =
            [
                QueryParameter("container_id", required: true),
                new AgentToolOperationParameter(
                    "If-Match",
                    AgentToolOperationParameterLocation.Header,
                    true,
                    AgentToolOperationValueSchema.Text),
            ],
        });

        var result = await tool.ExecuteAsync(
            """{"query":{"container_id":"oc_1"}}""");

        result.Should().Contain("NYXID_OPERATION_HEADER_MISSING");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldMatchAdmittedHeaderNamesCaseInsensitively()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(ListMessagesAdmission() with
        {
            Parameters =
            [
                QueryParameter("container_id", required: true),
                new AgentToolOperationParameter(
                    "If-Match",
                    AgentToolOperationParameterLocation.Header,
                    true,
                    AgentToolOperationValueSchema.Text),
            ],
        });

        var result = await tool.ExecuteAsync(
            """{"query":{"container_id":"oc_1"},"headers":{"if-match":"etag-alpha"}}""");

        result.Should().NotContain("NYXID_OPERATION_HEADER_MISSING");
        handler.ProxyRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDispatchCommittedProofWithoutLiveServiceDiscovery()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(ListMessagesAdmission(), "organization-token");

        var result = await tool.ExecuteAsync(
            """{"query":{"container_id":"oc_1"}}""");

        result.Should().NotContain("error_code");
        handler.RequestUris.Should().ContainSingle();
        handler.RequestUris.Should().OnlyContain(uri =>
            !uri.Contains("/api/v1/keys", StringComparison.Ordinal));
        handler.ProxyRequests.Should().ContainSingle();
        handler.AuthorizationBearers.Should().ContainSingle().Which.Should().Be("user-token");
    }

    [Theory]
    [InlineData("If-Match", "")]
    [InlineData("If-Match", "etag\rsmuggled")]
    [InlineData("If-Match", "etag\nsmuggled")]
    [InlineData("Accept", "text/plain")]
    public async Task ExecuteAsync_ShouldRejectInvalidAdmittedHeadersBeforeAnyHttpRequest(
        string name,
        string value)
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(ListMessagesAdmission() with
        {
            Parameters =
            [
                QueryParameter("container_id", required: true),
                new AgentToolOperationParameter(
                    name,
                    AgentToolOperationParameterLocation.Header,
                    false,
                    AgentToolOperationValueSchema.Text),
            ],
        });
        var arguments = JsonSerializer.Serialize(new
        {
            query = new { container_id = "oc_1" },
            headers = new Dictionary<string, string> { [name] = value },
        });

        var result = await tool.ExecuteAsync(arguments);

        result.Should().Contain("NYXID_OPERATION_HEADER_INVALID");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectOversizedConditionalHeaderBeforeAnyHttpRequest()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(ListMessagesAdmission() with
        {
            Parameters =
            [
                QueryParameter("container_id", required: true),
                new AgentToolOperationParameter(
                    "If-None-Match",
                    AgentToolOperationParameterLocation.Header,
                    false,
                    AgentToolOperationValueSchema.Text),
            ],
        });
        var arguments = JsonSerializer.Serialize(new
        {
            query = new { container_id = "oc_1" },
            headers = new Dictionary<string, string> { ["If-None-Match"] = new string('x', 1025) },
        });

        var result = await tool.ExecuteAsync(arguments);

        result.Should().Contain("NYXID_OPERATION_HEADER_INVALID");
        handler.RequestCount.Should().Be(0);
    }

    [Theory]
    [InlineData("""{"path_params":{"message_id":"om_1"}}""", "NYXID_OPERATION_PATH_PARAMETER_MISSING")]
    [InlineData("""{"path_params":{"message_id":"om_1","file_key":"f1","extra":"x"}}""", "NYXID_OPERATION_PATH_PARAMETER_UNKNOWN")]
    [InlineData("""{"path_params":{"message_id":"a/b","file_key":"f1"}}""", "NYXID_OPERATION_PATH_PARAMETER_INVALID")]
    [InlineData("""{"path_params":{"message_id":"a%2Fb","file_key":"f1"}}""", "NYXID_OPERATION_PATH_PARAMETER_INVALID")]
    [InlineData("""{"path_params":{"message_id":"..","file_key":"f1"}}""", "NYXID_OPERATION_PATH_PARAMETER_INVALID")]
    [InlineData("""{"path_params":{"message_id":"%2e%2e","file_key":"f1"}}""", "NYXID_OPERATION_PATH_PARAMETER_INVALID")]
    [InlineData("""{"path_params":{"message_id":"%252e%252e","file_key":"f1"}}""", "NYXID_OPERATION_PATH_PARAMETER_INVALID")]
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

    private static AgentToolOperationParameter QueryParameter(
        string name,
        bool required,
        AgentToolOperationValueSchema? schema = null) =>
        new(name, AgentToolOperationParameterLocation.Query, required, schema ?? AgentToolOperationValueSchema.Text);

    private static AgentToolOperationValueSchema TextSchema(params string[] allowedValues) =>
        ScalarSchema(AgentToolOperationValueKind.String, allowedValues);

    private static AgentToolOperationValueSchema ScalarSchema(
        AgentToolOperationValueKind kind,
        IReadOnlyList<string>? allowedValues = null) =>
        new(
            kind,
            [],
            new HashSet<string>(StringComparer.Ordinal),
            null,
            allowedValues ?? [],
            false);

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

    private static AgentToolContextScope PushContext(
        AgentToolOperationAdmission admission,
        string? organizationToken = null) =>
        AgentToolContextScope.Push(new AgentToolExecutionContext(
            AgentToolRequestIdentity.Empty,
            new AgentToolCredentials("user-token", organizationToken, null),
            new AgentToolCallerContext("scope-alpha", null, null),
            AgentToolChannelContext.Empty,
            AgentToolSenderBindingContext.Empty,
            LLMRequestRoutingContext.Empty,
            AgentToolConnectedServicesContext.Empty,
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal))
        {
            OperationAdmission = admission,
            WorkflowRuntime = new AgentWorkflowRuntimeContext(
                "workflow-run-actor-alpha",
                "run-alpha",
                "step-alpha",
                "run-alpha",
                1),
            InvocationSurface = AgentToolInvocationSurface.WorkflowToolCall,
        });

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

        public List<string> AuthorizationBearers { get; } = [];

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
            AuthorizationBearers.Add(request.Headers.Authorization?.Parameter ?? string.Empty);
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/v1/proxy/", StringComparison.Ordinal))
            {
                ProxyRequests.Add(new RecordedProxyRequest(
                    request.Method.Method,
                    request.RequestUri.AbsolutePath,
                    request.RequestUri.Query,
                    body));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = binaryResponse
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
