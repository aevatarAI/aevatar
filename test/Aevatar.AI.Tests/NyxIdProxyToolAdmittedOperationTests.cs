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
using FluentAssertions;

namespace Aevatar.AI.Tests;

/// <summary>
/// Covers the proof-bound main chain: the committed operation proof owns routing, and every
/// rejection must happen before a single NyxID request leaves the process.
/// </summary>
public sealed class NyxIdProxyToolAdmittedOperationTests
{
    private const string CatalogDigest =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

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

    [Fact]
    public void AdmittedRequestBuilder_ShouldBuildAuthoredRequestOnlyFromDeclaredValues()
    {
        var result = NyxIdAdmittedRequestBuilder.Build(
            AuthoredRequestAdmission(),
            """{"path_params":{"event_id":"evt alpha"},"query":{"notify":"owner"},"headers":{"If-Match":"etag-alpha"},"body":{"title":"Planning"}}""");

        result.Succeeded.Should().BeTrue();
        result.Failure.Should().BeNull();
        result.Request.Should().BeEquivalentTo(new NyxIdOperationRequest(
            "us-calendar-alpha",
            "calendar-alpha",
            "POST",
            "/events/evt%20alpha?notify=owner",
            """{"title":"Planning"}""",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["If-Match"] = "etag-alpha",
            },
            false));
    }

    [Fact]
    public void AdmittedRequestBuilder_ShouldDeriveAuthoredFileArtifactModeFromAdmission()
    {
        var admission = AuthoredRequestAdmission() with
        {
            HttpMethod = "GET",
            RequestBody = null,
            ResponsePolicy = new AgentToolOperationResponsePolicy(false, true, []),
        };

        var result = NyxIdAdmittedRequestBuilder.Build(
            admission,
            """{"path_params":{"event_id":"evt-alpha"}}""");

        result.Succeeded.Should().BeTrue();
        result.Request!.FileArtifact.Should().BeTrue();
    }

    [Theory]
    [InlineData("text")]
    [InlineData("file_artifact")]
    public void AdmittedRequestBuilder_ShouldRejectAuthoredResponseModeOverride(string responseMode)
    {
        var result = NyxIdAdmittedRequestBuilder.Build(
            AuthoredRequestAdmission(),
            JsonSerializer.Serialize(new
            {
                path_params = new { event_id = "evt-alpha" },
                response_mode = responseMode,
            }));

        result.Succeeded.Should().BeFalse();
        result.Failure!.Code.Should().Be("NYXID_OPERATION_ARGUMENT_NOT_SUPPORTED");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void AdmittedRequestBuilder_ShouldRejectAmbiguousAuthoredResponsePolicy(
        bool textAllowed,
        bool fileArtifactAllowed)
    {
        var admission = AuthoredRequestAdmission() with
        {
            ResponsePolicy = new AgentToolOperationResponsePolicy(
                textAllowed,
                fileArtifactAllowed,
                []),
        };

        var result = NyxIdAdmittedRequestBuilder.Build(
            admission,
            """{"path_params":{"event_id":"evt-alpha"}}""");

        result.Succeeded.Should().BeFalse();
        result.Failure!.Code.Should().Be("NYXID_OPERATION_RESPONSE_POLICY_INVALID");
    }

    [Theory]
    [InlineData("""{"service_id":"us-override"}""")]
    [InlineData("""{"slug":"slug-override"}""")]
    [InlineData("""{"endpoint_id":"endpoint-override"}""")]
    [InlineData("""{"request_contract_digest":"sha256:override"}""")]
    [InlineData("""{"method":"DELETE"}""")]
    [InlineData("""{"path":"/override"}""")]
    [InlineData("""{"path_template":"/override/{id}"}""")]
    [InlineData("""{"response_policy":{"text_allowed":true}}""")]
    [InlineData("""{"execution_policy":{"risk":"read_only"}}""")]
    public void AdmittedRequestBuilder_ShouldRejectAuthoredRouteIdentityOrPolicyOverride(
        string argumentsJson)
    {
        var result = NyxIdAdmittedRequestBuilder.Build(
            AuthoredRequestAdmission(),
            argumentsJson);

        result.Succeeded.Should().BeFalse();
        result.Failure!.Code.Should().Be("NYXID_OPERATION_ARGUMENT_NOT_SUPPORTED");
    }

    [Theory]
    [InlineData("""{"path_params":{"event_id":"evt-alpha","other":"blocked"}}""", "NYXID_OPERATION_PATH_PARAMETER_UNKNOWN")]
    [InlineData("""{"path_params":{"event_id":"evt-alpha"},"query":{"other":"blocked"}}""", "NYXID_OPERATION_QUERY_PARAMETER_UNKNOWN")]
    [InlineData("""{"path_params":{"event_id":"evt-alpha"},"headers":{"other":"blocked"}}""", "NYXID_OPERATION_HEADER_NOT_DECLARED")]
    public void AdmittedRequestBuilder_ShouldRejectUndeclaredAuthoredValueSlot(
        string argumentsJson,
        string errorCode)
    {
        var result = NyxIdAdmittedRequestBuilder.Build(
            AuthoredRequestAdmission(),
            argumentsJson);

        result.Succeeded.Should().BeFalse();
        result.Failure!.Code.Should().Be(errorCode);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("%2fsecret")]
    [InlineData("evt%20alpha")]
    public void AdmittedRequestBuilder_ShouldRejectUnsafeOrPreEncodedAuthoredPathSegment(
        string eventId)
    {
        var result = NyxIdAdmittedRequestBuilder.Build(
            AuthoredRequestAdmission(),
            JsonSerializer.Serialize(new { path_params = new { event_id = eventId } }));

        result.Succeeded.Should().BeFalse();
        result.Failure!.Code.Should().Be("NYXID_OPERATION_PATH_PARAMETER_INVALID");
    }

    [Theory]
    [InlineData("""{"path_params":{"event_id":"evt-alpha"},"query":{"notify":7}}""", "NYXID_OPERATION_QUERY_PARAMETER_INVALID")]
    [InlineData("""{"path_params":{"event_id":"evt-alpha"},"headers":{"If-Match":false}}""", "NYXID_OPERATION_HEADER_INVALID")]
    public void AdmittedRequestBuilder_ShouldRejectNonStringAuthoredQueryOrHeader(
        string argumentsJson,
        string errorCode)
    {
        var result = NyxIdAdmittedRequestBuilder.Build(
            AuthoredRequestAdmission(),
            argumentsJson);

        result.Succeeded.Should().BeFalse();
        result.Failure!.Code.Should().Be(errorCode);
    }

    [Theory]
    [InlineData(
        AgentToolOperationParameterLocation.Query,
        AgentToolOperationValueKind.Integer,
        """{"path_params":{"event_id":"evt-alpha"},"query":{"notify":7}}""",
        "NYXID_OPERATION_QUERY_PARAMETER_INVALID")]
    [InlineData(
        AgentToolOperationParameterLocation.Header,
        AgentToolOperationValueKind.Boolean,
        """{"path_params":{"event_id":"evt-alpha"},"headers":{"If-Match":true}}""",
        "NYXID_OPERATION_HEADER_INVALID")]
    public void AdmittedRequestBuilder_ShouldRejectNonStringAuthoredQueryOrHeader_WhenSchemaMatchesScalar(
        AgentToolOperationParameterLocation location,
        AgentToolOperationValueKind kind,
        string argumentsJson,
        string errorCode)
    {
        var admission = AuthoredRequestAdmission();
        admission = admission with
        {
            Parameters = admission.Parameters.Select(parameter =>
                parameter.Location == location
                    ? parameter with { Schema = ScalarSchema(kind) }
                    : parameter).ToArray(),
        };

        var result = NyxIdAdmittedRequestBuilder.Build(admission, argumentsJson);

        result.Succeeded.Should().BeFalse();
        result.Failure!.Code.Should().Be(errorCode);
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
    public async Task ExecuteAsync_ShouldRevalidateCommittedProofAgainstLiveMcpCatalog()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(ListMessagesAdmission(), "organization-token");

        var result = await tool.ExecuteAsync(
            """{"query":{"container_id":"oc_1"}}""");

        result.Should().NotContain("error_code");
        handler.McpConfigRequests.Should().ContainSingle();
        handler.ProxyRequests.Should().ContainSingle();
        handler.AuthorizationBearers.Should().OnlyContain(static bearer => bearer == "user-token");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDispatchAuthoredTextThroughExactRouteWithoutCatalogReads()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(AuthoredRequestAdmission());

        var result = await tool.ExecuteAsync(
            """{"path_params":{"event_id":"evt-runtime"},"query":{"notify":"owner"},"body":{"title":"Planning"}}""");

        result.Should().NotContain("error_code");
        handler.RequestCount.Should().Be(1);
        handler.McpConfigRequests.Should().BeEmpty();
        handler.RequestUris.Should().OnlyContain(uri =>
            !uri.Contains("/api/v1/keys", StringComparison.Ordinal) &&
            !uri.Contains("/api/v1/mcp/", StringComparison.Ordinal));
        var request = handler.ProxyRequests.Should().ContainSingle().Subject;
        request.Method.Should().Be("POST");
        request.Path.Should().Be("/api/v1/proxy/s/calendar-alpha/events/evt-runtime");
        request.Query.Should().Be("?_nyxid_via=us-calendar-alpha&notify=owner");
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_ShouldReceiptPublishedTextSuccessWithExactService()
    {
        var handler = new RecordingHandler
        {
            ProxyStatusCode = HttpStatusCode.OK,
            ProxyResponseBody = """{"error":true,"status":503,"body":"domain payload"}""",
        };
        var tool = CreateTool(handler);
        using var scope = PushContext(ListMessagesAdmission());

        var outcome = await ((IAgentTool)tool).ExecuteWithOutcomeAsync(
            "call-published",
            tool.Name,
            """{"query":{"container_id":"oc_1"}}""");

        outcome.Receipt.Should().NotBeNull();
        outcome.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        outcome.Receipt.SubjectKind.Should().Be("nyxid.user-service");
        outcome.Receipt.SubjectId.Should().Be("us-lark-alpha");
        handler.ProxyRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_ShouldNotReceiptRejectedProofArguments()
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(AuthoredRequestAdmission());

        var outcome = await ((IAgentTool)tool).ExecuteWithOutcomeAsync(
            "call-rejected",
            tool.Name,
            """{"path":"/forged"}""");

        outcome.ResultJson.Should().Contain("NYXID_OPERATION_ARGUMENT_NOT_SUPPORTED");
        outcome.Receipt.Should().BeNull();
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldDispatchAuthoredFileThroughExactRouteWithoutCatalogReads()
    {
        var handler = new RecordingHandler(binaryResponse: true);
        var ingress = new RecordingFileArtifactIngress();
        var tool = CreateTool(handler, ingress);
        var admission = AuthoredRequestAdmission() with
        {
            HttpMethod = "GET",
            RequestBody = null,
            ResponsePolicy = new AgentToolOperationResponsePolicy(false, true, []),
            ExecutionPolicy = ReadOnlyPolicy(),
        };
        using var scope = PushContext(admission);

        var result = await tool.ExecuteAsync(
            """{"path_params":{"event_id":"evt-runtime"}}""");

        result.Should().Contain("\"success\":true");
        handler.RequestCount.Should().Be(1);
        handler.McpConfigRequests.Should().BeEmpty();
        handler.RequestUris.Should().OnlyContain(uri =>
            !uri.Contains("/api/v1/keys", StringComparison.Ordinal) &&
            !uri.Contains("/api/v1/mcp/", StringComparison.Ordinal));
        handler.ProxyRequests.Should().ContainSingle().Which.Query
            .Should().Be("?_nyxid_via=us-calendar-alpha");
        ingress.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_ShouldReceiptAuthoredFileSuccessWithExactService()
    {
        var handler = new RecordingHandler(binaryResponse: true);
        var ingress = new RecordingFileArtifactIngress();
        var tool = CreateTool(handler, ingress);
        var admission = AuthoredRequestAdmission() with
        {
            HttpMethod = "GET",
            RequestBody = null,
            ResponsePolicy = new AgentToolOperationResponsePolicy(false, true, []),
            ExecutionPolicy = ReadOnlyPolicy(),
        };
        using var scope = PushContext(admission);

        var outcome = await ((IAgentTool)tool).ExecuteWithOutcomeAsync(
            "call-file",
            tool.Name,
            """{"path_params":{"event_id":"evt-runtime"}}""");

        outcome.ResultJson.Should().Contain("\"success\":true");
        outcome.Receipt.Should().NotBeNull();
        outcome.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        outcome.Receipt.SubjectKind.Should().Be("nyxid.user-service");
        outcome.Receipt.SubjectId.Should().Be("us-calendar-alpha");
        ingress.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "bad_request", 1000, "Bad request: _nyxid_via UserService 'us-calendar-alpha' has slug 'calendar-beta', but the route requested 'calendar-alpha'", "NYXID_OPERATION_AUTHORITY_DRIFT")]
    [InlineData(HttpStatusCode.NotFound, "not_found", 1003, "Not found: UserService 'us-calendar-alpha' not found", "NYXID_OPERATION_AUTHORITY_DRIFT")]
    [InlineData(HttpStatusCode.Forbidden, "org_role_insufficient", 8103, "Organization role insufficient: you do not have proxy access to this service", "NYXID_OPERATION_AUTHORITY_ACCESS_DENIED")]
    public async Task ExecuteAsync_ShouldMapAuthoredExactRouteAuthorityFailureWithoutFallback(
        HttpStatusCode status,
        string error,
        int errorCode,
        string message,
        string expectedOperationErrorCode)
    {
        var handler = new RecordingHandler
        {
            ProxyStatusCode = status,
            ProxyResponseBody = JsonSerializer.Serialize(new
            {
                error,
                error_code = errorCode,
                message,
            }),
        };
        var tool = CreateTool(handler);
        using var scope = PushContext(AuthoredRequestAdmission());

        var result = await tool.ExecuteAsync(
            """{"path_params":{"event_id":"evt-runtime"},"body":{"title":"Planning"}}""");

        result.Should().Contain(expectedOperationErrorCode);
        handler.RequestCount.Should().Be(1);
        handler.McpConfigRequests.Should().BeEmpty();
        handler.ProxyRequests.Should().ContainSingle().Which.Query
            .Should().Be("?_nyxid_via=us-calendar-alpha");
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_ShouldReceiptAuthoredExactRouteAuthorityFailure()
    {
        var handler = new RecordingHandler
        {
            ProxyStatusCode = HttpStatusCode.NotFound,
            ProxyResponseBody = JsonSerializer.Serialize(new
            {
                error = "not_found",
                error_code = 1003,
                message = "Not found: UserService 'us-calendar-alpha' not found",
            }),
        };
        var tool = CreateTool(handler);
        using var scope = PushContext(AuthoredRequestAdmission());

        var outcome = await ((IAgentTool)tool).ExecuteWithOutcomeAsync(
            "call-authority-drift",
            tool.Name,
            """{"path_params":{"event_id":"evt-runtime"},"body":{"title":"Planning"}}""");

        outcome.ResultJson.Should().Contain("NYXID_OPERATION_AUTHORITY_DRIFT");
        outcome.Receipt.Should().NotBeNull();
        outcome.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        outcome.Receipt.ErrorCode.Should().Be("NYXID_OPERATION_AUTHORITY_DRIFT");
        outcome.Receipt.SubjectKind.Should().Be("nyxid.user-service");
        outcome.Receipt.SubjectId.Should().Be("us-calendar-alpha");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "bad_request", 1000, "downstream request was invalid")]
    [InlineData(HttpStatusCode.NotFound, "not_found", 1003, "downstream resource was not found")]
    [InlineData(HttpStatusCode.NotFound, "not_found", 1003, "Not found: UserService 'us-other' not found")]
    [InlineData(HttpStatusCode.BadRequest, "bad_request", 1000, "Bad request: _nyxid_via UserService 'us-calendar-alpha' has slug 'calendar-beta', but the route requested 'calendar-other'")]
    [InlineData(HttpStatusCode.BadRequest, "bad_request", 1000, "Bad request: _nyxid_via UserService 'us-calendar-alpha' has slug 'calendar-beta', but the route requested 'calendar-alpha' unexpected suffix")]
    public async Task ExecuteAsync_ShouldNotMapOrdinaryAuthoredDownstreamTextFailure(
        HttpStatusCode status,
        string error,
        int errorCode,
        string message)
    {
        var handler = new RecordingHandler
        {
            ProxyStatusCode = status,
            ProxyResponseBody = JsonSerializer.Serialize(new { error, error_code = errorCode, message }),
        };
        var tool = CreateTool(handler);
        using var scope = PushContext(AuthoredRequestAdmission());

        var result = await tool.ExecuteAsync(
            """{"path_params":{"event_id":"evt-runtime"},"body":{"title":"Planning"}}""");

        result.Should().NotContain("NYXID_OPERATION_AUTHORITY_");
        result.Should().Contain($"\"status\": {(int)status}");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotMapSuccessfulAuthoredDownstreamTextBody()
    {
        const string downstreamBody = """{"error":"not_found","error_code":1003,"message":"Not found: UserService 'us-calendar-alpha' not found"}""";
        var handler = new RecordingHandler
        {
            ProxyStatusCode = HttpStatusCode.OK,
            ProxyResponseBody = downstreamBody,
        };
        var tool = CreateTool(handler);
        using var scope = PushContext(AuthoredRequestAdmission());

        var result = await tool.ExecuteAsync(
            """{"path_params":{"event_id":"evt-runtime"},"body":{"title":"Planning"}}""");

        result.Equals(downstreamBody, StringComparison.Ordinal).Should().BeTrue();
        result.Should().NotContain("NYXID_OPERATION_AUTHORITY_");
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_ShouldNotMapSuccessfulAuthoredProxyShapedPayload()
    {
        const string downstreamBody =
            """{"error":true,"status":404,"body":"{\"error\":\"not_found\",\"error_code\":1003,\"message\":\"Not found: UserService 'us-calendar-alpha' not found\"}"}""";
        var handler = new RecordingHandler
        {
            ProxyStatusCode = HttpStatusCode.OK,
            ProxyResponseBody = downstreamBody,
        };
        var tool = CreateTool(handler);
        using var scope = PushContext(AuthoredRequestAdmission());

        var outcome = await ((IAgentTool)tool).ExecuteWithOutcomeAsync(
            "call-domain-payload",
            tool.Name,
            """{"path_params":{"event_id":"evt-runtime"},"body":{"title":"Planning"}}""");

        outcome.ResultJson.Equals(downstreamBody, StringComparison.Ordinal).Should().BeTrue();
        outcome.Receipt.Should().NotBeNull();
        outcome.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        outcome.Receipt.SubjectId.Should().Be("us-calendar-alpha");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "bad_request", 1000, "Bad request: _nyxid_via UserService 'us-calendar-alpha' has slug 'calendar-beta', but the route requested 'calendar-alpha'", "NYXID_OPERATION_AUTHORITY_DRIFT")]
    [InlineData(HttpStatusCode.NotFound, "not_found", 1003, "Not found: UserService 'us-calendar-alpha' not found", "NYXID_OPERATION_AUTHORITY_DRIFT")]
    [InlineData(HttpStatusCode.Forbidden, "org_role_insufficient", 8103, "Organization role insufficient: you do not have proxy access to this service", "NYXID_OPERATION_AUTHORITY_ACCESS_DENIED")]
    public async Task ExecuteAsync_ShouldKeepAuthoredFileExactRouteAuthorityFailureOutOfArtifactIngress(
        HttpStatusCode status,
        string error,
        int errorCode,
        string message,
        string expectedOperationErrorCode)
    {
        var handler = new RecordingHandler(binaryResponse: true)
        {
            ProxyStatusCode = status,
            ProxyResponseBody = JsonSerializer.Serialize(new { error, error_code = errorCode, message }),
        };
        var ingress = new RecordingFileArtifactIngress();
        var tool = CreateTool(handler, ingress);
        var admission = AuthoredRequestAdmission() with
        {
            HttpMethod = "GET",
            RequestBody = null,
            ResponsePolicy = new AgentToolOperationResponsePolicy(false, true, []),
            ExecutionPolicy = ReadOnlyPolicy(),
        };
        using var scope = PushContext(admission);

        var result = await tool.ExecuteAsync(
            """{"path_params":{"event_id":"evt-runtime"}}""");

        result.Should().Contain(expectedOperationErrorCode);
        handler.RequestCount.Should().Be(1);
        handler.McpConfigRequests.Should().BeEmpty();
        handler.ProxyRequests.Should().ContainSingle().Which.Query
            .Should().Be("?_nyxid_via=us-calendar-alpha");
        ingress.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepAuthoredFileDownstreamFailureOnArtifactPolicy()
    {
        var handler = new RecordingHandler(binaryResponse: true)
        {
            ProxyStatusCode = HttpStatusCode.NotFound,
            ProxyResponseBody = """{"error":"not_found","error_code":1003,"message":"downstream resource was not found"}""",
        };
        var ingress = new RecordingFileArtifactIngress();
        var tool = CreateTool(handler, ingress);
        var admission = AuthoredRequestAdmission() with
        {
            HttpMethod = "GET",
            RequestBody = null,
            ResponsePolicy = new AgentToolOperationResponsePolicy(false, true, []),
            ExecutionPolicy = ReadOnlyPolicy(),
        };
        using var scope = PushContext(admission);

        var result = await tool.ExecuteAsync(
            """{"path_params":{"event_id":"evt-runtime"}}""");

        result.Should().Contain("provider_binary_download_failed");
        result.Should().NotContain("NYXID_OPERATION_AUTHORITY_");
        ingress.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepPublishedFileAuthorityFailureOnArtifactPolicy()
    {
        var handler = new RecordingHandler(binaryResponse: true)
        {
            ProxyStatusCode = HttpStatusCode.NotFound,
            ProxyResponseBody = """{"error":"not_found","error_code":1003,"message":"UserService not found"}""",
        };
        var ingress = new RecordingFileArtifactIngress();
        var tool = CreateTool(handler, ingress);
        using var scope = PushContext(MessageResourceAdmission());

        var result = await tool.ExecuteAsync(
            """{"path_params":{"message_id":"om-runtime","file_key":"file-runtime"},"response_mode":"file_artifact"}""");

        result.Should().Contain("provider_binary_download_failed");
        result.Should().NotContain("NYXID_OPERATION_AUTHORITY_DRIFT");
        handler.McpConfigRequests.Should().ContainSingle();
        handler.ProxyRequests.Should().ContainSingle();
        ingress.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectLiveRouteDriftBeforeProxyDispatch()
    {
        var handler = new RecordingHandler
        {
            McpConfigJson = McpConfig(ListMessagesAdmission() with { ServiceSlug = "api-lark-bot-v2" }),
        };
        var tool = CreateTool(handler);
        using var scope = PushContext(ListMessagesAdmission());

        var result = await tool.ExecuteAsync(
            """{"query":{"container_id":"oc_1"}}""");

        result.Should().Contain("NYXID_OPERATION_AUTHORITY_DRIFT");
        handler.McpConfigRequests.Should().ContainSingle();
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectLiveContractDigestDriftBeforeProxyDispatch()
    {
        var handler = new RecordingHandler
        {
            McpConfigJson = McpConfig(ListMessagesAdmission() with { PathTemplate = "/open-apis/im/v2/messages" }),
        };
        var tool = CreateTool(handler);
        using var scope = PushContext(ListMessagesAdmission());

        var result = await tool.ExecuteAsync(
            """{"query":{"container_id":"oc_1"}}""");

        result.Should().Contain("NYXID_OPERATION_CONTRACT_DRIFT");
        handler.McpConfigRequests.Should().ContainSingle();
        handler.ProxyRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("_nyxid_via")]
    [InlineData("_NYXID_ROUTE")]
    public async Task ExecuteAsync_ShouldRejectReservedProofQueryNamesBeforeAnyHttpRequest(string name)
    {
        var handler = new RecordingHandler();
        var tool = CreateTool(handler);
        using var scope = PushContext(ListMessagesAdmission() with
        {
            Parameters = [QueryParameter(name, required: false)],
        });

        var result = await tool.ExecuteAsync("{}");

        result.Should().Contain("NYXID_OPERATION_QUERY_PARAMETER_FORBIDDEN");
        handler.RequestCount.Should().Be(0);
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
            new AgentToolOperationIdentity.PublishedEndpoint("lark_get_message_resource"),
            AgentToolOperationAuthorizationBasis.PublishedContract,
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

    private static AgentToolOperationAdmission AuthoredRequestAdmission() =>
        new(
            "us-calendar-alpha",
            "calendar-alpha",
            new AgentToolOperationIdentity.AuthoredRequest(
                "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            AgentToolOperationAuthorizationBasis.ExplicitRequest,
            "POST",
            "/events/{event_id}",
            "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            [
                PathParameter("event_id"),
                QueryParameter("notify", required: false),
                new AgentToolOperationParameter(
                    "If-Match",
                    AgentToolOperationParameterLocation.Header,
                    false,
                    AgentToolOperationValueSchema.Text),
            ],
            new AgentToolOperationRequestBody(
                false,
                "application/json",
                new AgentToolOperationValueSchema(
                    AgentToolOperationValueKind.Object,
                    [],
                    new HashSet<string>(StringComparer.Ordinal),
                    null,
                    [],
                    true)),
            AgentToolOperationResponsePolicy.TextOnly,
            WritePolicy());

    private static AgentToolOperationAdmission ListMessagesAdmission()
    {
        var admission = new AgentToolOperationAdmission(
            "us-lark-alpha",
            "api-lark-bot-2",
            new AgentToolOperationIdentity.PublishedEndpoint("lark_list_messages"),
            AgentToolOperationAuthorizationBasis.PublishedContract,
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
        var catalog = NyxIdMcpOperationCatalog.Parse(
            McpConfig(admission),
            "test",
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMinutes(5));
        return admission with
        {
            ContractDigest = catalog.Services.Single().Endpoints.Single().ContractDigest,
        };
    }

    private static string McpConfig(AgentToolOperationAdmission admission) =>
        JsonSerializer.Serialize(new
        {
            contract_version = "1.0",
            catalog_digest = CatalogDigest,
            user_id = "nyx-user-alpha",
            services = new[]
            {
                new
                {
                    service_id = admission.ServiceInstanceId,
                    service_name = "Lark",
                    service_slug = admission.ServiceSlug,
                    is_user_service = true,
                    is_generic_proxy = false,
                    endpoints = new[]
                    {
                        new
                        {
                            endpoint_id = PublishedEndpointId(admission),
                            name = PublishedEndpointId(admission),
                            method = admission.HttpMethod,
                            path = admission.PathTemplate,
                            parameters = admission.Parameters.Select(static parameter => new
                            {
                                name = parameter.Name,
                                @in = parameter.Location.ToString().ToLowerInvariant(),
                                required = parameter.Required,
                                schema = new { type = parameter.Schema.Kind.ToString().ToLowerInvariant() },
                            }),
                            request_body_schema = admission.RequestBody is null
                                ? null
                                : SchemaJson(admission.RequestBody.Schema),
                            request_content_type = admission.RequestBody?.MediaType,
                            request_body_required = admission.RequestBody?.Required ?? false,
                            response = new
                            {
                                content_types = admission.ResponsePolicy.MediaTypes,
                                binary_artifact = admission.ResponsePolicy.FileArtifactAllowed
                                    ? true
                                    : admission.ResponsePolicy.TextAllowed
                                        ? false
                                        : (bool?)null,
                            },
                        },
                    },
                },
            },
        });

    private static object SchemaJson(AgentToolOperationValueSchema schema)
    {
        var result = new Dictionary<string, object?>
        {
            ["type"] = schema.Kind.ToString().ToLowerInvariant(),
        };
        if (schema.Kind == AgentToolOperationValueKind.Object)
        {
            result["properties"] = schema.Properties.ToDictionary(
                static property => property.Name,
                static property => SchemaJson(property.Schema),
                StringComparer.Ordinal);
            result["required"] = schema.RequiredProperties;
            result["additionalProperties"] = schema.AdditionalPropertiesAllowed;
        }
        else if (schema.Kind == AgentToolOperationValueKind.Array)
        {
            result["items"] = SchemaJson(schema.Items!);
        }
        else if (schema.AllowedValues.Count > 0)
        {
            result["enum"] = schema.AllowedValues;
        }

        return result;
    }

    private static AgentToolOperationAdmission WithLiveDigest(AgentToolOperationAdmission admission)
    {
        var catalog = NyxIdMcpOperationCatalog.Parse(
            McpConfig(admission),
            "test",
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMinutes(5));
        var endpoint = catalog.Services.SingleOrDefault()?.Endpoints.SingleOrDefault();
        return endpoint is null ? admission : admission with
        {
            ContractDigest = endpoint.ContractDigest,
        };
    }

    private static AgentToolOperationAdmission CreateApprovalAdmission() =>
        new(
            "us-lark-alpha",
            "api-lark-bot-2",
            new AgentToolOperationIdentity.PublishedEndpoint("lark_create_approval_instance"),
            AgentToolOperationAuthorizationBasis.PublishedContract,
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
            new AgentToolOperationIdentity.PublishedEndpoint("lark_get_approval_instance"),
            AgentToolOperationAuthorizationBasis.PublishedContract,
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
            new AgentToolOperationIdentity.PublishedEndpoint("get_item"),
            AgentToolOperationAuthorizationBasis.PublishedContract,
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

    private static string PublishedEndpointId(AgentToolOperationAdmission admission) =>
        admission.Identity is AgentToolOperationIdentity.PublishedEndpoint published
            ? published.EndpointId
            : throw new InvalidOperationException("Published endpoint admission expected.");

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
            OperationAdmission = admission.Identity is AgentToolOperationIdentity.PublishedEndpoint
                ? WithLiveDigest(admission)
                : admission,
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
        public string? McpConfigJson { get; init; }

        public HttpStatusCode? ProxyStatusCode { get; init; }

        public string? ProxyResponseBody { get; init; }

        public int RequestCount { get; private set; }

        public List<RecordedProxyRequest> ProxyRequests { get; } = [];

        public List<string> RequestBodies { get; } = [];

        public List<string> RequestUris { get; } = [];

        public List<string> AuthorizationBearers { get; } = [];

        public List<string> McpConfigRequests { get; } = [];

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
            if (request.RequestUri!.AbsolutePath == "/api/v1/mcp/config")
            {
                McpConfigRequests.Add(request.RequestUri.AbsolutePath);
                var admission = AgentToolRequestContext.Current?.OperationAdmission ?? ListMessagesAdmission();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        McpConfigJson ?? McpConfig(admission),
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            if (request.RequestUri.AbsolutePath.StartsWith("/api/v1/proxy/", StringComparison.Ordinal))
            {
                ProxyRequests.Add(new RecordedProxyRequest(
                    request.Method.Method,
                    request.RequestUri.AbsolutePath,
                    request.RequestUri.Query,
                    body));

                if (ProxyStatusCode is { } proxyStatusCode)
                {
                    return new HttpResponseMessage(proxyStatusCode)
                    {
                        Content = new StringContent(
                            ProxyResponseBody ?? string.Empty,
                            Encoding.UTF8,
                            "application/json"),
                    };
                }
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

}
