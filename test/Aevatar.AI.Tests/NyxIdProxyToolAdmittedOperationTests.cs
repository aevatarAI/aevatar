using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
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
            new AgentToolOperationResponsePolicy(false, true, ["application/octet-stream"]));

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
            AgentToolOperationResponsePolicy.TextOnly);

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
            AgentToolOperationResponsePolicy.TextOnly);

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
            AgentToolOperationResponsePolicy.TextOnly);

    private static AgentToolOperationParameter PathParameter(string name) =>
        new(name, AgentToolOperationParameterLocation.Path, true, AgentToolOperationValueSchema.Text);

    private static AgentToolOperationParameter QueryParameter(string name, bool required) =>
        new(name, AgentToolOperationParameterLocation.Query, required, AgentToolOperationValueSchema.Text);

    private static NyxIdProxyTool CreateTool(
        RecordingHandler handler,
        INyxIdProxyFileArtifactIngress? ingress = null) =>
        new(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(handler)),
            fileArtifactIngress: ingress);

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
            OperationAdmission = admission,
            WorkflowRuntime = new AgentWorkflowRuntimeContext(
                "workflow-run-actor-alpha",
                "run-alpha",
                "step-alpha",
                "run-alpha",
                1),
        });

    private sealed record RecordedProxyRequest(string Method, string Path, string Query, string Body);

    private sealed class RecordingHandler(bool binaryResponse = false) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public List<RecordedProxyRequest> ProxyRequests { get; } = [];

        public List<string> RequestBodies { get; } = [];

        public List<string> RequestUris { get; } = [];

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
}
