using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.AI.Tests;

public sealed class NyxIdDelegationTokenLeaseTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 20, 16, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("direct-access-token")]
    [InlineData("nyx-api-key")]
    public async Task ResolveAsync_ShouldPassThroughNonJwtCredentialsWithoutNetwork(string token)
    {
        var handler = new RecordingHandler((_, _) =>
            throw new InvalidOperationException("Non-JWT credentials must not be refreshed."));
        using var lease = CreateLease(handler, new FakeTimeProvider(Now));

        var result = await lease.ResolveAsync(token);

        result.Should().BeEquivalentTo(NyxIdDelegationTokenLeaseResult.Success(token));
        handler.RefreshCount.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_ShouldShareOneProactiveRefreshAcrossConcurrentCalls()
    {
        var originalToken = CreateDelegationToken(Now.AddSeconds(60));
        var refreshedToken = CreateDelegationToken(Now.AddMinutes(5));
        var refreshEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (request, ct) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/v1/delegation/refresh");
            refreshEntered.TrySetResult();
            await releaseRefresh.Task.WaitAsync(ct);
            return JsonResponse(new
            {
                access_token = refreshedToken,
                token_type = "Bearer",
                expires_in = 300,
                scope = "proxy:*",
            });
        });
        using var lease = CreateLease(handler, new FakeTimeProvider(Now));

        var resolutions = Enumerable.Range(0, 10)
            .Select(_ => lease.ResolveAsync(originalToken))
            .ToArray();
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseRefresh.TrySetResult();

        var results = await Task.WhenAll(resolutions);

        results.Should().OnlyContain(result =>
            result.Succeeded && result.AccessToken == refreshedToken);
        handler.RefreshCount.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_ShouldRenewTheLatestTokenAcrossMultipleExpiryWindows()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var originalToken = CreateDelegationToken(Now.AddSeconds(60));
        var firstRefreshedToken = CreateDelegationToken(Now.AddMinutes(5));
        var secondRefreshedToken = CreateDelegationToken(Now.AddMinutes(10));
        var refreshResponses = new Queue<string>(
            [firstRefreshedToken, secondRefreshedToken]);
        var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse(new
        {
            access_token = refreshResponses.Dequeue(),
            token_type = "Bearer",
            expires_in = 300,
            scope = "proxy:*",
        })));
        using var lease = CreateLease(handler, timeProvider);

        (await lease.ResolveAsync(originalToken)).AccessToken.Should().Be(firstRefreshedToken);
        timeProvider.Advance(TimeSpan.FromMinutes(4));
        (await lease.ResolveAsync(originalToken)).AccessToken.Should().Be(secondRefreshedToken);

        handler.RefreshCount.Should().Be(2);
        handler.Requests.Where(request => request.Path == "/api/v1/delegation/refresh")
            .Select(request => request.BearerToken)
            .Should().Equal(originalToken, firstRefreshedToken);
    }

    [Fact]
    public async Task ResolveAsync_ShouldShareTypedFailureWithoutUsingExpiredCredential()
    {
        var originalToken = CreateDelegationToken(Now.AddSeconds(60));
        var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse(
            new { error = "unauthorized", error_code = 1001 },
            HttpStatusCode.Unauthorized)));
        using var lease = CreateLease(handler, new FakeTimeProvider(Now));

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => lease.ResolveAsync(originalToken)));

        results.Should().OnlyContain(result =>
            !result.Succeeded &&
            result.ErrorCode == NyxIdDelegationTokenLease.RefreshFailedErrorCode);
        handler.RefreshCount.Should().Be(1);
    }

    [Theory]
    [InlineData("raw_text")]
    [InlineData("admitted_text")]
    [InlineData("file_artifact")]
    public async Task ProxyTool_ShouldLeaseBeforeEveryNyxIdProxyPath(string executionPath)
    {
        var originalToken = CreateDelegationToken(Now.AddSeconds(60));
        var refreshedToken = CreateDelegationToken(Now.AddMinutes(5));
        var admission = PublishedReadAdmission();
        var mcpConfig = McpConfig(admission);
        if (executionPath == "admitted_text")
            admission = WithLiveDigest(admission, mcpConfig);
        var handler = new RecordingHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/delegation/refresh" => JsonResponse(new
                {
                    access_token = refreshedToken,
                    token_type = "Bearer",
                    expires_in = 300,
                    scope = "proxy:*",
                }),
                "/api/v1/mcp/config" => JsonResponse(mcpConfig),
                _ when executionPath == "file_artifact" => BinaryResponse(),
                _ => JsonResponse(new { ok = true }),
            }));
        using var lease = CreateLease(handler, new FakeTimeProvider(Now));
        var client = CreateClient(handler);
        var ingress = new RecordingFileArtifactIngress();
        var tool = new NyxIdProxyTool(
            client,
            fileArtifactIngress: ingress,
            delegationTokenLease: lease);
        using var context = PushContext(
            originalToken,
            executionPath == "admitted_text" ? admission : null);

        var result = executionPath switch
        {
            "admitted_text" => (await tool.ExecuteWithOutcomeAsync(
                "call-alpha",
                tool.Name,
                """{"query":{"container_id":"oc-alpha"}}""")).ResultJson,
            "file_artifact" => await tool.ExecuteAsync(
                """{"slug":"files-alpha","service_id":"service-alpha","path":"/report.pdf","response_mode":"file_artifact"}"""),
            _ => await tool.ExecuteAsync(
                """{"slug":"items-alpha","service_id":"service-alpha","path":"/items"}"""),
        };

        using var resultDocument = JsonDocument.Parse(result);
        resultDocument.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        handler.RefreshCount.Should().Be(1);
        handler.Requests
            .Where(request => request.Path != "/api/v1/delegation/refresh")
            .Should().OnlyContain(request => request.BearerToken == refreshedToken);
        if (executionPath == "admitted_text")
        {
            handler.Requests.Select(request => request.Path).Should().ContainInOrder(
                "/api/v1/delegation/refresh",
                "/api/v1/mcp/config");
        }
        if (executionPath == "file_artifact")
            ingress.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ProxyTool_ShouldReturnTypedFailureWithoutSendingProxyRequest()
    {
        var originalToken = CreateDelegationToken(Now.AddSeconds(60));
        var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse(
            new { error = "unauthorized", error_code = 1001 },
            HttpStatusCode.Unauthorized)));
        using var lease = CreateLease(handler, new FakeTimeProvider(Now));
        var tool = new NyxIdProxyTool(
            CreateClient(handler),
            delegationTokenLease: lease);
        using var context = PushContext(originalToken);

        var outcome = await tool.ExecuteWithOutcomeAsync(
            "call-alpha",
            tool.Name,
            """{"slug":"items-alpha","service_id":"service-alpha","path":"/items"}""");
        var receipt = tool.CreateResultReceipt(
            "call-alpha",
            tool.Name,
            """{"slug":"items-alpha","service_id":"service-alpha","path":"/items"}""",
            outcome.ResultJson);

        using var result = JsonDocument.Parse(outcome.ResultJson);
        result.RootElement.GetProperty("error_code").GetString()
            .Should().Be(NyxIdDelegationTokenLease.RefreshFailedErrorCode);
        receipt!.ErrorCode.Should().Be(NyxIdDelegationTokenLease.RefreshFailedErrorCode);
        handler.Requests.Should().ContainSingle(request =>
            request.Path == "/api/v1/delegation/refresh");
    }

    private static NyxIdDelegationTokenLease CreateLease(
        RecordingHandler handler,
        TimeProvider timeProvider) =>
        new(CreateClient(handler), timeProvider);

    private static NyxIdApiClient CreateClient(RecordingHandler handler) =>
        new(
            new NyxIdToolOptions { BaseUrl = "https://nyx.test" },
            new HttpClient(handler));

    private static AgentToolContextScope PushContext(
        string accessToken,
        AgentToolOperationAdmission? admission = null) =>
        AgentToolContextScope.Push(AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(accessToken, null, null),
            Caller = new AgentToolCallerContext("scope-alpha", null, null),
            WorkflowRuntime = new AgentWorkflowRuntimeContext(
                "workflow-run-actor-alpha",
                "run-alpha",
                "step-alpha",
                "run-alpha",
                1),
            OperationAdmission = admission,
            InvocationSurface = AgentToolInvocationSurface.WorkflowToolCall,
        });

    private static AgentToolOperationAdmission PublishedReadAdmission() =>
        new(
            "us-lark-alpha",
            "api-lark-bot-2",
            new AgentToolOperationIdentity.PublishedEndpoint("lark_list_messages"),
            AgentToolOperationAuthorizationBasis.PublishedContract,
            "GET",
            "/open-apis/im/v1/messages",
            "sha256:placeholder",
            [
                new AgentToolOperationParameter(
                    "container_id",
                    AgentToolOperationParameterLocation.Query,
                    true,
                    AgentToolOperationValueSchema.Text),
                new AgentToolOperationParameter(
                    "page_size",
                    AgentToolOperationParameterLocation.Query,
                    false,
                    AgentToolOperationValueSchema.Text),
            ],
            null,
            AgentToolOperationResponsePolicy.TextOnly,
            new AgentToolOperationExecutionPolicy(
                AgentToolOperationRisk.ReadOnly,
                AgentToolOperationApproval.None,
                AgentToolOperationEnforcementOwner.Aevatar,
                [AgentToolOperationExecutionMode.Interactive]));

    private static string McpConfig(AgentToolOperationAdmission admission) =>
        JsonSerializer.Serialize(new
        {
            contract_version = "1.0",
            catalog_digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            user_id = "nyx-user-alpha",
            services = new[]
            {
                new
                {
                    service_id = admission.ServiceInstanceId,
                    service_name = "Items",
                    service_slug = admission.ServiceSlug,
                    is_user_service = true,
                    is_generic_proxy = false,
                    endpoints = new[]
                    {
                        new
                        {
                            endpoint_id = "lark_list_messages",
                            name = "lark_list_messages",
                            method = admission.HttpMethod,
                            path = admission.PathTemplate,
                            parameters = admission.Parameters.Select(static parameter => new
                            {
                                name = parameter.Name,
                                @in = parameter.Location.ToString().ToLowerInvariant(),
                                required = parameter.Required,
                                schema = new
                                {
                                    type = parameter.Schema.Kind.ToString().ToLowerInvariant(),
                                },
                            }),
                            request_body_schema = (object?)null,
                            request_content_type = (string?)null,
                            request_body_required = false,
                            response = new
                            {
                                content_types = admission.ResponsePolicy.MediaTypes,
                                binary_artifact = false,
                            },
                        },
                    },
                },
            },
        });

    private static AgentToolOperationAdmission WithLiveDigest(
        AgentToolOperationAdmission admission,
        string mcpConfig)
    {
        var catalog = NyxIdMcpOperationCatalog.Parse(
            mcpConfig,
            "test",
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMinutes(5));
        var endpoint = catalog.Services.SingleOrDefault()?.Endpoints.SingleOrDefault() ??
                       throw new InvalidOperationException(string.Join(
                           "; ",
                           catalog.Issues.Select(issue => $"{issue.Code}:{issue.SafeMessage}")));
        return admission with
        {
            ContractDigest = endpoint.ContractDigest,
        };
    }

    private static string CreateDelegationToken(DateTimeOffset expiresAt)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "none" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            delegated = true,
            exp = expiresAt.ToUnixTimeSeconds(),
        }));
        return $"{header}.{payload}.signature";
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static HttpResponseMessage JsonResponse(
        object value,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(
                value is string json ? json : JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json"),
        };

    private static HttpResponseMessage BinaryResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]),
        };

    private sealed record RecordedRequest(string Path, string BearerToken);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        private int _refreshCount;

        public int RefreshCount => Volatile.Read(ref _refreshCount);
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/delegation/refresh")
                Interlocked.Increment(ref _refreshCount);
            lock (Requests)
            {
                Requests.Add(new RecordedRequest(
                    path,
                    request.Headers.Authorization?.Parameter ?? string.Empty));
            }

            return await responder(request, cancellationToken);
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
                FileId = "file-alpha",
                ArtifactId = "artifact-alpha",
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
