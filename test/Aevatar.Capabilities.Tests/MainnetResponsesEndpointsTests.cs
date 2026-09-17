using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.Authentication.Hosting;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.Mainnet.Host.Api.Responses;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetResponsesEndpointsTests
{
    [Fact]
    public async Task PostResponses_WithJsonRequest_ShouldReturnCompletedResponseAndPassRequestScopedBearer()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk
                {
                    DeltaContent = "pong",
                    IsLast = true,
                    Usage = new TokenUsage(3, 2, 5),
                },
            ],
        };
        var sessions = new RecordingResponseSessionStore();
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""
            {
              "model": "gpt-5.4",
              "input": "ping",
              "stream": false,
              "temperature": 0.2,
              "max_output_tokens": 128
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Headers.Add(ResponsesApiEndpoints.NyxIdIdentityTokenHeader, "identity-token");
        request.Headers.Add(ResponsesApiEndpoints.NyxIdDelegationTokenHeader, "delegation-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain("secret-token");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("id").GetString().Should().StartWith("resp_");
        var responseId = root.GetProperty("id").GetString()!;
        root.GetProperty("object").GetString().Should().Be("response");
        root.GetProperty("status").GetString().Should().Be("completed");
        root.GetProperty("model").GetString().Should().Be("gpt-5.4");
        root.GetProperty("max_output_tokens").GetInt32().Should().Be(128);
        root.GetProperty("temperature").GetDouble().Should().Be(0.2);
        root.GetProperty("parallel_tool_calls").GetBoolean().Should().BeTrue();
        root.GetProperty("reasoning").GetProperty("effort").ValueKind.Should().Be(JsonValueKind.Null);
        AssertCompletedMessage(root, "pong");
        root.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(3);
        root.GetProperty("usage").GetProperty("output_tokens").GetInt32().Should().Be(2);
        root.GetProperty("usage").GetProperty("total_tokens").GetInt32().Should().Be(5);

        provider.StreamCallCount.Should().Be(1);
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Model.Should().Be("gpt-5.4");
        provider.LastRequest.MaxTokens.Should().Be(128);
        provider.LastRequest.Temperature.Should().Be(0.2);
        provider.LastRequest.Messages.Should().ContainSingle();
        provider.LastRequest.Messages[0].Content.Should().Be("ping");
        provider.LastRequest.RequestId.Should().Be(responseId);
        provider.LastRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.RequestId);
        provider.LastRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ScopeId);
        provider.LastRequest.CallerContext.Should().Be(new LLMRequestCallerContext(
            "user-1",
            "user-1",
            responseId,
            new LLMRequestCallerCredentials("secret-token")));
        provider.LastRequest.ToolContext.Should().NotBeNull();
        provider.LastRequest.ToolContext!.Caller.OwnerScopeId.Should().Be("user-1");
        // The NyxID bearer is carried on the typed CallerContext.Credentials channel,
        // NOT through LLMRequest.Metadata. Metadata is the log-shaped string-keyed bag
        // that telemetry sinks may serialize; secret material belongs out-of-band.
        // Tool providers read the bearer from AgentToolRequestContext (separate path).
        provider.LastRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        provider.LastRequest.CallerContext!.Credentials!.NyxIdBearer.Should().Be("secret-token");
        var callerScopeResolver = app.Services.GetRequiredService<IResponsesCallerScopeResolver>()
            .Should()
            .BeOfType<StubResponsesCallerScopeResolver>()
            .Subject;
        callerScopeResolver.LastContext.Should().Be(new ResponsesCallerScopeResolutionContext(
            "secret-token",
            "identity-token",
            "delegation-token"));

        sessions.Registered.Should().ContainSingle();
        sessions.Registered[0].ScopeId.Should().Be("user-1");
        sessions.Registered[0].OwnerSubject.Should().Be("user-1");
        sessions.Registered[0].OriginKind.Should().Be(LlmSessionOriginKind.ApiKey);
        var snapshot = await sessions.GetByResponseIdAsync(responseId);
        snapshot!.ActorId.Should().NotContain(responseId);
    }

    [Fact]
    public async Task PostResponses_WhenCompletionReadModelLags_ShouldWaitAndReturnCompletedResponse()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk
                {
                    DeltaContent = "eventually visible",
                    IsLast = true,
                },
            ],
        };
        var sessions = new RecordingResponseSessionStore
        {
            CompletionObservationLagReads = 1,
        };
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"gpt-5.4","input":"ping","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");
        AssertCompletedMessage(doc.RootElement, "eventually visible");
    }

    [Fact]
    public async Task PostResponses_WithStreamTrue_ShouldReturnResponsesSseFrames()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "po" },
                new LLMStreamChunk
                {
                    DeltaContent = "ng",
                    IsLast = true,
                    Usage = new TokenUsage(3, 2, 5),
                },
            ],
        };
        var sessions = new RecordingResponseSessionStore();
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"gpt-5.4","input":"ping","stream":true}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "stream-secret");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        body.Should().Contain("event: response.created");
        body.Should().Contain("event: response.output_item.added");
        body.Should().Contain("\"type\":\"response.output_text.delta\"");
        body.Should().Contain("\"delta\":\"po\"");
        body.Should().Contain("\"delta\":\"ng\"");
        body.Should().Contain("event: response.output_text.done");
        body.Should().Contain("event: response.output_item.done");
        body.Should().Contain("event: response.completed");
        body.Should().Contain("\"text\":\"pong\"");
        body.Should().NotContain("stream-secret");

        provider.StreamCallCount.Should().Be(1);
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        provider.LastRequest.CallerContext!.Credentials!.NyxIdBearer.Should().Be("stream-secret");
        sessions.RecordedCompletions.Should().BeEmpty();
    }

    [Fact]
    public async Task PostResponses_WhenNonStreamObservationTimesOut_ShouldReturnTimeoutEnvelope()
    {
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();
        await using var app = await CreateAppAsync(
            provider,
            sessions,
            observationService: StaticLlmSessionRunObservationService.Error(
                LlmSessionRunObservedTerminalKind.TimedOut,
                StatusCodes.Status504GatewayTimeout,
                "response_timeout",
                "Timed out waiting 30 seconds for the LLM run to emit a terminal event."));
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"gpt-5.4","input":"ping","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout, body);
        GetErrorCode(body).Should().Be("response_timeout");
        body.Should().NotContain("\"status\":\"in_progress\"");
        body.Should().NotContain("\"object\":\"response\"");
        sessions.StatusUpdates.Should().BeEmpty();
    }

    [Fact]
    public async Task PostResponses_WithDeclaredToolCall_ShouldPersistForwardedToolCallAndReturnFunctionCallItem()
    {
        const string parametersJson = """{"type":"object","properties":{"city":{"type":"string"}}}""";
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_weather_1",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":"Singapore"}""",
                    },
                    IsLast = true,
                },
            ],
        };
        var sessions = new RecordingResponseSessionStore();
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""
            {
              "model": "gpt-5.4",
              "input": "weather",
              "tools": [
                {
                  "type": "function",
                  "name": "get_weather",
                  "description": "Get weather by city.",
                  "parameters": {"type":"object","properties":{"city":{"type":"string"}}}
                }
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");
        AssertCompletedMessage(doc.RootElement, string.Empty);
        doc.RootElement.GetProperty("output").GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("output")[1].GetProperty("type").GetString().Should().Be("function_call");
        doc.RootElement.GetProperty("output")[1].GetProperty("call_id").GetString().Should().Be("call_weather_1");

        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Tools.Should().NotBeNull();
        provider.LastRequest.Tools!.Select(static tool => tool.Name)
            .Should().Equal("get_weather");
        var command = app.Services.GetRequiredService<ResponsesRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ToolSelection.ForwardedTools.Should().ContainSingle(tool => tool.ToolName == "get_weather");
        command.ToolSelection.OwnedToolNames.Should().BeEmpty();
        command.ToolSelection.OwnedCatalogProof.ToolCount.Should().Be(0);
        command.ToolSelection.ForwardedTools.Single(tool => tool.ToolName == "get_weather").SchemaHash
            .Should().Be(ResponsesToolSchemaHashes.Compute(parametersJson));
        sessions.ForwardedToolCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task PostResponses_WithSubstituteTool_ShouldRegisterAevatarToolAndNotForwardClientToolCall()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunkBatches =
            [
                [
                    new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = "call_task_1",
                            Name = "Task",
                            ArgumentsJson = """{"prompt":"work"}""",
                        },
                        IsLast = true,
                    },
                ],
                [
                    new LLMStreamChunk
                    {
                        DeltaContent = "delegated",
                        IsLast = true,
                    },
                ],
            ],
        };
        var sessions = new RecordingResponseSessionStore();
        var toolProvider = new RecordingResponsesToolProvider(
            [new StubAgentTool("Task", "Aevatar task dispatcher")],
            [new StubAgentTool("aevatar_notes", "Aevatar notes")]);
        var catalogPlanner = new FixedResponsesOwnedToolCatalogPlanner(
            ToolSetNames.WorkspaceDefault,
            new StubAgentTool("Task", "Aevatar task dispatcher"),
            new StubAgentTool("aevatar_invoke_gagent", "Invoke a GAgent"));
        await using var app = await CreateAppAsync(
            provider,
            sessions,
            responsesToolProvider: toolProvider,
            ownedToolCatalogPlanner: catalogPlanner);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""
            {
              "model": "gpt-5.4",
              "input": "delegate work",
              "tools": [
                {
                  "type": "function",
                  "name": "Task",
                  "description": "Client task tool",
                  "parameters": {"type":"object","properties":{"prompt":{"type":"string"}}}
                }
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        provider.StreamCallCount.Should().Be(1);
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Tools.Should().NotBeNull();
        provider.LastRequest.Tools!.Select(static tool => tool.Name)
            .Should().Contain(["Task", "aevatar_invoke_gagent"])
            .And.NotContain("aevatar_notes");
        var taskTool = provider.LastRequest.Tools.Single(static tool => tool.Name == "Task");
        taskTool.Description.Should().Be("Task substitute");
        taskTool.ParametersSchema.Should().Be("""{"type":"object","properties":{}}""");
        provider.LastRequest.Messages.Should().ContainSingle()
            .Which.Content.Should().Be("delegate work");
        sessions.ForwardedToolCalls.Should().BeEmpty();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");
        AssertCompletedMessage(doc.RootElement, string.Empty);
        doc.RootElement.GetProperty("output").GetArrayLength().Should().Be(1);
        body.Should().NotContain("\"type\":\"function_call\"");
        body.Should().NotContain("call_task_1");
    }

    [Fact]
    public async Task AevatarSubstituteTools_ShouldPersistTodoWithoutRegisteringFakeTask()
    {
        var commandPort = new RecordingResponsesAgentToolStatePort();
        var provider = CreateResponsesAevatarToolProvider(commandPort);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.ScopeId] = "scope-1",
            [LLMRequestMetadataKeys.OwnerSubject] = "owner-1",
            [LLMRequestMetadataKeys.ResponseId] = "resp_1",
        };
        var previous = AgentToolRequestContext.Current;
        var context = BuildToolProviderContext(
            new ResponsesCallerScope("scope-1", "owner-1", LlmSessionOriginKind.ApiKey),
            "resp_1",
            "token");
        try
        {
            AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(metadata);
            var substituteTools = await provider.GetSubstituteToolsAsync(context);
            var todoTool = substituteTools.Single(x => x.Name == "TodoWrite");
            var todoResult = await todoTool.ExecuteAsync(
                """{"todos":[{"id":"todo-1","content":"Ship prototype","status":"in_progress"}]}""");

            todoResult.Should().Contain("stored");
            substituteTools.Select(static tool => tool.Name)
                .Should()
                .Contain("TodoWrite")
                .And.NotContain(["Task", "task"]);
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }

        commandPort.TodoWrites.Should().ContainSingle();
        commandPort.TodoWrites[0].ScopeId.Should().Be("scope-1");
        commandPort.TodoWrites[0].OwnerSubject.Should().Be("owner-1");
        commandPort.TodoWrites[0].SourceResponseId.Should().Be("resp_1");
    }

    [Fact]
    public async Task AevatarWebFetchSubstitute_ShouldUseCachedReadModelAndRecordTrace()
    {
        var commandPort = new RecordingResponsesAgentToolStatePort();
        var cacheKey = commandPort.SeedWebCache(
            "WebFetch",
            "https://example.com/docs",
            """{"url":"https://example.com/docs","content":"cached"}""");
        var provider = CreateResponsesAevatarToolProvider(commandPort);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.ScopeId] = "scope-1",
            [LLMRequestMetadataKeys.OwnerSubject] = "owner-1",
            [LLMRequestMetadataKeys.ResponseId] = "resp_1",
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token",
        };
        var previous = AgentToolRequestContext.Current;
        var context = BuildToolProviderContext(
            new ResponsesCallerScope("scope-1", "owner-1", LlmSessionOriginKind.ApiKey),
            "resp_1",
            "token");
        try
        {
            AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(metadata);
            var fetchTool = (await provider.GetSubstituteToolsAsync(context)).Single(x => x.Name == "WebFetch");
            var result = await fetchTool.ExecuteAsync("""{"url":"https://example.com/docs"}""");

            result.Should().Contain("cached");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }

        commandPort.WebTraces.Should().ContainSingle();
        commandPort.WebTraces[0].Trace.CacheKey.Should().Be(cacheKey);
        commandPort.WebTraces[0].Trace.CacheHit.Should().BeTrue();
    }

    [Fact]
    public async Task AevatarWebSearchSubstitute_ShouldUseCachedReadModelAndRecordTrace()
    {
        var commandPort = new RecordingResponsesAgentToolStatePort();
        var cacheKey = commandPort.SeedWebCache(
            "WebSearch",
            "aevatar docs\n3",
            """{"results":[{"title":"cached docs"}]}""");
        var provider = CreateResponsesAevatarToolProvider(commandPort);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.ScopeId] = "scope-1",
            [LLMRequestMetadataKeys.OwnerSubject] = "owner-1",
            [LLMRequestMetadataKeys.ResponseId] = "resp_1",
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token",
        };
        var previous = AgentToolRequestContext.Current;
        var context = BuildToolProviderContext(
            new ResponsesCallerScope("scope-1", "owner-1", LlmSessionOriginKind.ApiKey),
            "resp_1",
            "token");
        try
        {
            AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(metadata);
            var searchTool = (await provider.GetSubstituteToolsAsync(context)).Single(x => x.Name == "WebSearch");
            var result = await searchTool.ExecuteAsync("""{"query":"aevatar docs","max_results":3}""");

            result.Should().Contain("cached docs");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }

        commandPort.WebTraces.Should().ContainSingle();
        commandPort.WebTraces[0].Trace.CacheKey.Should().Be(cacheKey);
        commandPort.WebTraces[0].Trace.Query.Should().Be("aevatar docs");
        commandPort.WebTraces[0].Trace.CacheHit.Should().BeTrue();
    }

    [Fact]
    public async Task ResponsesUserSkillsToolProvider_ShouldBridgeOnlySkillRuntimeTools()
    {
        var services = new ServiceCollection();
        services.AddSkills(_ => { });
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(new NotFoundHttpMessageHandler())));
        services.AddOrnnSkills();
        services.AddSingleton<ResponsesUserSkillsToolProvider>();
        services.AddSingleton<IAgentToolSource>(new StubAgentToolSource(
            [new StubAgentTool("future_tool", "Future tool")]));

        await using var provider = services.BuildServiceProvider();
        var toolProvider = provider.GetRequiredService<ResponsesUserSkillsToolProvider>();

        var tools = await toolProvider.GetAdditiveToolsAsync(
            BuildToolProviderContext(
                new ResponsesCallerScope("scope-1", "owner-1", LlmSessionOriginKind.ApiKey),
                "resp_1",
                "token"));

        tools.Select(static tool => tool.Name)
            .Should()
            .Equal("use_skill", "ornn_search_skills");
    }

    [Fact]
    public async Task ResponsesUserSkillsToolProvider_WhenOneSourceDiscoveryFails_ShouldReturnOtherSourceTools()
    {
        var toolProvider = new ResponsesUserSkillsToolProvider(
            new ThrowingAgentToolSource(),
            new StubAgentToolSource(
            [
                new StubAgentTool("ornn_search_skills", "Search Ornn skill catalog"),
            ]));

        var tools = await toolProvider.GetAdditiveToolsAsync(
            BuildToolProviderContext(
                new ResponsesCallerScope("scope-1", "owner-1", LlmSessionOriginKind.ApiKey),
                "resp_1",
                "token"));

        tools.Select(static tool => tool.Name)
            .Should()
            .ContainSingle("ornn_search_skills");
    }

    [Fact]
    public async Task ResponsesUserSkillsToolProvider_WhenRequestIsCanceled_ShouldPropagateCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var toolProvider = new ResponsesUserSkillsToolProvider(
            new StubAgentToolSource(
            [
                new StubAgentTool("use_skill", "Run a registered skill body"),
            ]),
            new CanceledAgentToolSource());

        Func<Task> act = async () => await toolProvider.GetAdditiveToolsAsync(
            BuildToolProviderContext(
                new ResponsesCallerScope("scope-1", "owner-1", LlmSessionOriginKind.ApiKey),
                "resp_1",
                "token"),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PostResponses_WhenSkillBridgeProviderRegisteredWithoutReviewedCatalog_ShouldNotAutoInjectTools()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk
                {
                    DeltaContent = "ok",
                    IsLast = true,
                    Usage = new TokenUsage(1, 1, 2),
                },
            ],
        };
        var sessions = new RecordingResponseSessionStore();
        var bridgeProvider = new RecordingResponsesToolProvider(
            [],
            [
                new StubAgentTool("use_skill", "Run a registered skill body"),
                new StubAgentTool("ornn_search_skills", "Search Ornn skill catalog"),
                new StubAgentTool("ornn_publish_skill", "Publish Ornn skill catalog package"),
            ]);
        await using var app = await CreateAppAsync(provider, sessions, responsesToolProvider: bridgeProvider);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""
            {
              "model": "gpt-5.4",
              "input": "search a skill",
              "stream": false
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Tools.Should().BeNullOrEmpty();
        var command = app.Services.GetRequiredService<ResponsesRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ToolSelection.OwnedToolNames.Should().BeEmpty();
        command.ToolSelection.OwnedCatalogProof.ToolCount.Should().Be(0);
    }

    [Fact]
    public async Task PostResponses_WithFunctionCallOutput_ShouldPersistToolResultAndForwardToolMessage()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "done", IsLast = true },
            ],
        };
        var sessions = new RecordingResponseSessionStore();
        var schemaHash = ResponsesToolSchemaHashes.Compute("""{"type":"object"}""");
        sessions.Seed(new LlmSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            LlmSessionOriginKind.ApiKey,
            null,
            LlmSessionStatus.Completed,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_previous",
            2,
            "resp_previous:tool:call_1:emitted",
            [
                new LlmSessionForwardedToolCallSnapshot(
                    "call_1",
                    "get_weather",
                    schemaHash,
                    """{"city":"Singapore"}""",
                    LlmSessionForwardedToolCallStatus.Pending,
                    DateTimeOffset.UtcNow.AddHours(1),
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    null,
                    null),
            ]));
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent($$"""
            {
              "model": "gpt-5.4",
              "previous_response_id": "resp_previous",
              "input": [
                {
                  "type": "function_call_output",
                  "call_id": "call_1",
                  "schema_hash": "{{schemaHash}}",
                  "output": {"temperature": 28}
                }
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        sessions.ToolResults.Should().ContainSingle();
        sessions.ToolResults[0].CallId.Should().Be("call_1");
        sessions.ToolResults[0].SchemaHash.Should().Be(schemaHash);
        sessions.ToolResults[0].ResultJson.Should().Be("""{"temperature": 28}""");
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Messages.Should().HaveCount(2);
        provider.LastRequest.Messages[0].Role.Should().Be("assistant");
        provider.LastRequest.Messages[0].ToolCalls.Should().ContainSingle();
        provider.LastRequest.Messages[0].ToolCalls![0].Id.Should().Be("call_1");
        provider.LastRequest.Messages[1].Role.Should().Be("tool");
        provider.LastRequest.Messages[1].ToolCallId.Should().Be("call_1");
        provider.LastRequest.Messages[1].Content.Should().Be("""{"temperature": 28}""");
        sessions.ResolvedToolResults.Should().ContainSingle()
            .Which.CallId.Should().Be("call_1");
    }

    [Fact]
    public async Task PostResponses_WithPartialOutOfOrderToolResult_ShouldOnlyForwardReturnedCall()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "partial done", IsLast = true },
            ],
        };
        var sessions = new RecordingResponseSessionStore();
        var schemaHash = ResponsesToolSchemaHashes.Compute("""{"type":"object"}""");
        sessions.Seed(new LlmSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            LlmSessionOriginKind.ApiKey,
            null,
            LlmSessionStatus.Completed,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_previous",
            3,
            "resp_previous:tool:call_2:emitted",
            [
                new LlmSessionForwardedToolCallSnapshot(
                    "call_1",
                    "get_weather",
                    schemaHash,
                    """{"city":"Singapore"}""",
                    LlmSessionForwardedToolCallStatus.Pending,
                    DateTimeOffset.UtcNow.AddHours(1),
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    null,
                    null),
                new LlmSessionForwardedToolCallSnapshot(
                    "call_2",
                    "get_time",
                    schemaHash,
                    """{"city":"Singapore"}""",
                    LlmSessionForwardedToolCallStatus.Pending,
                    DateTimeOffset.UtcNow.AddHours(1),
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    null,
                    null),
            ]));
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent($$"""
            {
              "model": "gpt-5.4",
              "previous_response_id": "resp_previous",
              "input": [
                {
                  "type": "function_call_output",
                  "call_id": "call_2",
                  "schema_hash": "{{schemaHash}}",
                  "output": {"time": "12:00"}
                }
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        sessions.ToolResults.Should().ContainSingle()
            .Which.CallId.Should().Be("call_2");
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Messages[0].ToolCalls.Should().ContainSingle()
            .Which.Id.Should().Be("call_2");
        provider.LastRequest.Messages[1].ToolCallId.Should().Be("call_2");
        sessions.ResolvedToolResults.Should().ContainSingle()
            .Which.CallId.Should().Be("call_2");
        var snapshot = await sessions.GetByResponseIdAsync("resp_previous");
        snapshot!.ForwardedToolCalls!.Single(x => x.CallId == "call_1").Status
            .Should().Be(LlmSessionForwardedToolCallStatus.Pending);
    }

    [Fact]
    public async Task PostResponses_WithDuplicateResolvedToolResult_ShouldReturnWithoutCallingProvider()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "should not run", IsLast = true },
            ],
        };
        var sessions = new RecordingResponseSessionStore();
        var schemaHash = ResponsesToolSchemaHashes.Compute("""{"type":"object"}""");
        sessions.Seed(new LlmSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            LlmSessionOriginKind.ApiKey,
            null,
            LlmSessionStatus.Completed,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_previous",
            4,
            "resp_previous:tool:call_1:resolved",
            [
                new LlmSessionForwardedToolCallSnapshot(
                    "call_1",
                    "get_weather",
                    schemaHash,
                    """{"city":"Singapore"}""",
                    LlmSessionForwardedToolCallStatus.Resolved,
                    DateTimeOffset.UtcNow.AddHours(1),
                    """{"temperature":28}""",
                    DateTimeOffset.UtcNow.AddMinutes(-2),
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    DateTimeOffset.UtcNow),
            ]));
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent($$"""
            {
              "model": "gpt-5.4",
              "previous_response_id": "resp_previous",
              "input": [
                {
                  "type": "function_call_output",
                  "call_id": "call_1",
                  "schema_hash": "{{schemaHash}}",
                  "output": {"temperature": 28}
                }
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString()
            .Should()
            .Be("""{"temperature":28}""");
        provider.LastRequest.Should().BeNull();
        sessions.Registered.Should().ContainSingle()
            .Which.PreviousResponseId.Should().Be("resp_previous");
        sessions.RecordedCompletions.Should().ContainSingle()
            .Which.Completion.OutputText.Should().Be("""{"temperature":28}""");
        sessions.ToolResults.Should().BeEmpty();
        sessions.ResolvedToolResults.Should().BeEmpty();
    }

    [Fact]
    public async Task PostResponses_WithExpiredForwardedToolCall_ShouldReturnToolCallNotAvailable_WithoutCallingProvider()
    {
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();
        var schemaHash = ResponsesToolSchemaHashes.Compute("""{"type":"object"}""");
        sessions.Seed(new LlmSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            LlmSessionOriginKind.ApiKey,
            null,
            LlmSessionStatus.Completed,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_previous",
            3,
            "resp_previous:tool:call_1:expired",
            [
                new LlmSessionForwardedToolCallSnapshot(
                    "call_1",
                    "get_weather",
                    schemaHash,
                    """{"city":"Singapore"}""",
                    LlmSessionForwardedToolCallStatus.Expired,
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    """{"error":"tool_call_expired","call_id":"call_1"}""",
                    DateTimeOffset.UtcNow.AddHours(-1),
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    null),
            ]));
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent($$"""
            {
              "model": "gpt-5.4",
              "previous_response_id": "resp_previous",
              "input": [
                {
                  "type": "function_call_output",
                  "call_id": "call_1",
                  "schema_hash": "{{schemaHash}}",
                  "output": {"temperature": 28}
                }
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        GetErrorCode(body).Should().Be("tool_call_not_available");
        body.Should().NotContain("secret-token");
        sessions.ToolResults.Should().BeEmpty();
        sessions.ResolvedToolResults.Should().BeEmpty();
        sessions.Registered.Should().BeEmpty();
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponses_WithCancelledForwardedToolCall_ShouldReturnToolCallNotAvailable_WithoutCallingProvider()
    {
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();
        var schemaHash = ResponsesToolSchemaHashes.Compute("""{"type":"object"}""");
        sessions.Seed(new LlmSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            LlmSessionOriginKind.ApiKey,
            null,
            LlmSessionStatus.Completed,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_previous",
            3,
            "resp_previous:tool:call_1:cancelled",
            [
                new LlmSessionForwardedToolCallSnapshot(
                    "call_1",
                    "get_weather",
                    schemaHash,
                    """{"city":"Singapore"}""",
                    LlmSessionForwardedToolCallStatus.Cancelled,
                    DateTimeOffset.UtcNow.AddMinutes(30),
                    null,
                    DateTimeOffset.UtcNow.AddHours(-1),
                    null,
                    null),
            ]));
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent($$"""
            {
              "model": "gpt-5.4",
              "previous_response_id": "resp_previous",
              "input": [
                {
                  "type": "function_call_output",
                  "call_id": "call_1",
                  "schema_hash": "{{schemaHash}}",
                  "output": {"temperature": 28}
                }
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        GetErrorCode(body).Should().Be("tool_call_not_available");
        body.Should().NotContain("secret-token");
        sessions.ToolResults.Should().BeEmpty();
        sessions.ResolvedToolResults.Should().BeEmpty();
        sessions.Registered.Should().BeEmpty();
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponses_WithFunctionCallOutputSchemaMismatch_ShouldReturnBadRequest()
    {
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();
        sessions.Seed(new LlmSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            LlmSessionOriginKind.ApiKey,
            null,
            LlmSessionStatus.Completed,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_previous",
            2,
            "resp_previous:tool:call_1:emitted",
            [
                new LlmSessionForwardedToolCallSnapshot(
                    "call_1",
                    "get_weather",
                    "expected-hash",
                    "{}",
                    LlmSessionForwardedToolCallStatus.Pending,
                    DateTimeOffset.UtcNow.AddHours(1),
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    null,
                    null),
            ]));
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""
            {
              "model": "gpt-5.4",
              "previous_response_id": "resp_previous",
              "input": [
                {
                  "type": "function_call_output",
                  "call_id": "call_1",
                  "schema_hash": "different-hash",
                  "output": "{}"
                }
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("tool_schema_hash_mismatch");
        sessions.ToolResults.Should().BeEmpty();
        sessions.Registered.Should().BeEmpty();
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponses_WithPreviousResponseId_ShouldRegisterLinkedSession()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "continued", IsLast = true },
            ],
        };
        var sessions = new RecordingResponseSessionStore();
        sessions.Seed(new LlmSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            LlmSessionOriginKind.ApiKey,
            null,
            LlmSessionStatus.Completed,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_previous",
            1,
            "resp_previous:registered"));
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""
            {
              "model": "gpt-5.4",
              "input": "continue",
              "previous_response_id": "resp_previous"
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("previous_response_id").GetString().Should().Be("resp_previous");
        sessions.Registered.Should().ContainSingle();
        sessions.Registered[0].PreviousResponseId.Should().Be("resp_previous");
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Messages.Should().ContainSingle();
        provider.LastRequest.Messages[0].Role.Should().Be("user");
        provider.LastRequest.Messages[0].Content.Should().Be("continue");
    }

    [Fact]
    public async Task PostResponses_WithExpiredPreviousResponse_ShouldRejectResume()
    {
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();
        sessions.Seed(new LlmSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            LlmSessionOriginKind.ApiKey,
            null,
            LlmSessionStatus.Completed,
            DateTimeOffset.UtcNow.AddHours(-2),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_previous",
            1,
            "resp_previous:registered"));
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"gpt-5.4","input":"continue","previous_response_id":"resp_previous"}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        GetErrorCode(body).Should().Be("previous_response_expired");
        sessions.Registered.Should().BeEmpty();
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponses_WithCancelledPreviousResponse_ShouldReturnStructuredNotAvailableError_WithoutCallingProvider()
    {
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();
        sessions.Seed(new LlmSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            LlmSessionOriginKind.ApiKey,
            null,
            LlmSessionStatus.Cancelled,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_previous",
            2,
            "resp_previous:status:cancelled"));
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"gpt-5.4","input":"continue","previous_response_id":"resp_previous"}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        GetErrorCode(body).Should().Be("previous_response_not_available");
        body.Should().NotContain("secret-token");
        sessions.Registered.Should().BeEmpty();
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponses_WithPreviousResponseFromDifferentScope_ShouldReturnForbidden()
    {
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();
        sessions.Seed(new LlmSessionSnapshot(
            "resp_foreign",
            "other-user",
            "other-user",
            LlmSessionOriginKind.ApiKey,
            null,
            LlmSessionStatus.Completed,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_foreign",
            1,
            "resp_foreign:registered"));
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"gpt-5.4","input":"continue","previous_response_id":"resp_foreign"}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        GetErrorCode(body).Should().Be("response_scope_mismatch");
        sessions.Registered.Should().BeEmpty();
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponses_WithPreviousResponseAfterBearerScopeRotation_ShouldReResolveCurrentBearerAndRejectBeforeRegistrationOrProviderCall()
    {
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();
        var schemaHash = ResponsesToolSchemaHashes.Compute("""{"type":"object"}""");
        sessions.Seed(new LlmSessionSnapshot(
            "resp_previous",
            "old-scope",
            "owner-1",
            LlmSessionOriginKind.ApiKey,
            null,
            LlmSessionStatus.Completed,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_previous",
            2,
            "resp_previous:tool:call_1:emitted",
            [
                new LlmSessionForwardedToolCallSnapshot(
                    "call_1",
                    "get_weather",
                    schemaHash,
                    """{"city":"Singapore"}""",
                    LlmSessionForwardedToolCallStatus.Pending,
                    DateTimeOffset.UtcNow.AddHours(1),
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    null,
                    null),
            ]));
        var callerScopeResolver = new TokenAwareResponsesCallerScopeResolver(
            new ResponsesCallerScope("new-scope", "owner-1", LlmSessionOriginKind.ApiKey));
        await using var app = await CreateAppAsync(provider, sessions, callerScopeResolver);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent($$"""
            {
              "model": "gpt-5.4",
              "previous_response_id": "resp_previous",
              "input": [
                {
                  "type": "function_call_output",
                  "call_id": "call_1",
                  "schema_hash": "{{schemaHash}}",
                  "output": {"temperature": 28}
                }
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "rotated-secret");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        GetErrorCode(body).Should().Be("response_scope_mismatch");
        callerScopeResolver.ResolvedTokens.Should().Equal("rotated-secret");
        sessions.Registered.Should().BeEmpty();
        sessions.ToolResults.Should().BeEmpty();
        sessions.ResolvedToolResults.Should().BeEmpty();
        sessions.ForwardedToolCalls.Should().BeEmpty();
        sessions.RecordedCompletions.Should().BeEmpty();
        sessions.StatusUpdates.Should().BeEmpty();
        provider.LastRequest.Should().BeNull();
        provider.StreamCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PostResponses_WithPreviousResponseFromDifferentOrigin_ShouldReturnForbidden()
    {
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();
        sessions.Seed(new LlmSessionSnapshot(
            "resp_channel",
            "user-1",
            "user-1",
            LlmSessionOriginKind.Channel,
            null,
            LlmSessionStatus.Completed,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_channel",
            1,
            "resp_channel:registered"));
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"gpt-5.4","input":"continue","previous_response_id":"resp_channel"}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        GetErrorCode(body).Should().Be("response_origin_mismatch");
        sessions.Registered.Should().BeEmpty();
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponsesCancel_ShouldCancelRunThroughSessionActor()
    {
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();
        sessions.Seed(new LlmSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            LlmSessionOriginKind.ApiKey,
            null,
            LlmSessionStatus.Accepted,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_previous",
            2,
            "resp_previous:tool:call_1:emitted",
            [
                new LlmSessionForwardedToolCallSnapshot(
                    "call_1",
                    "get_weather",
                    "schema-1",
                    "{}",
                    LlmSessionForwardedToolCallStatus.Pending,
                    DateTimeOffset.UtcNow.AddHours(1),
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    null,
                    null),
            ]));
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses/resp_previous/cancel");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Headers.Add(ResponsesApiEndpoints.NyxIdIdentityTokenHeader, "identity-token");
        request.Headers.Add(ResponsesApiEndpoints.NyxIdDelegationTokenHeader, "delegation-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("id").GetString().Should().Be("resp_previous");
        doc.RootElement.GetProperty("status").GetString().Should().Be("cancelled");
        sessions.CancelledRuns.Should().ContainSingle()
            .Which.Should().Be(("response-session:resp_previous", "resp_previous", "resp_previous:llm-run"));
        var snapshot = await sessions.GetByResponseIdAsync("resp_previous");
        snapshot!.Status.Should().Be(LlmSessionStatus.Cancelled);
        snapshot.ForwardedToolCalls.Should().ContainSingle()
            .Which.Status.Should().Be(LlmSessionForwardedToolCallStatus.Cancelled);
        var callerScopeResolver = app.Services.GetRequiredService<IResponsesCallerScopeResolver>()
            .Should()
            .BeOfType<StubResponsesCallerScopeResolver>()
            .Subject;
        callerScopeResolver.LastContext.Should().Be(new ResponsesCallerScopeResolutionContext(
            "secret-token",
            "identity-token",
            "delegation-token"));
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponsesCancel_WithExpiredResponse_ShouldReturnStructuredExpiredError()
    {
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();
        sessions.Seed(new LlmSessionSnapshot(
            "resp_expired",
            "user-1",
            "user-1",
            LlmSessionOriginKind.ApiKey,
            null,
            LlmSessionStatus.Expired,
            DateTimeOffset.UtcNow.AddHours(-2),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_expired",
            2,
            "resp_expired:status:5"));
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses/resp_expired/cancel");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        GetErrorCode(body).Should().Be("response_expired");
        body.Should().NotContain("secret-token");
        sessions.StatusUpdates.Should().BeEmpty();
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponses_WithoutBearer_ShouldReturnUnauthorized()
    {
        var provider = new RecordingLLMProvider();
        await using var app = await CreateAppAsync(provider);
        var client = app.GetTestClient();

        var response = await client.PostAsync(
            "/v1/responses",
            JsonContent("""{"model":"gpt-5.4","input":"ping"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("authentication_required");
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponses_WhenHostAuthEnabled_ShouldReachEndpointHandlerNotJwtBearerChallenge()
    {
        // Regression: PR #625 originally shipped without `.AllowAnonymous()`, so the
        // host's FallbackPolicy.RequireAuthenticatedUser() (installed by
        // AddAevatarAuthentication) rejected NyxID API keys — which are opaque
        // non-JWT tokens — with an empty 401 from JwtBearer's default challenge,
        // before the endpoint's manual ExtractBearerToken / NyxID `/me` path ran.
        // The other tests use a bare host (no AddAevatarAuthentication), so they
        // can't catch this. Here we wire the real auth pipeline and assert the
        // handler still runs (proved by its structured `authentication_required`
        // JSON body) rather than producing the empty-body JwtBearer challenge.
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // Production env keeps Authentication:Enabled forced true.
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        // Authority is irrelevant — the request has no Authorization header, so
        // JwtBearer never reaches OIDC discovery. We only need the FallbackPolicy
        // and JwtBearer scheme to be registered so an un-annotated endpoint would
        // be rejected. .AllowAnonymous() must short-circuit that.
        builder.Configuration["Aevatar:Authentication:Authority"] = "https://invalid.example";
        builder.Configuration["Aevatar:Authentication:Audience"] = "aevatar-api";
        builder.AddAevatarAuthentication();

        builder.Services.AddSingleton(provider);
        builder.Services.AddSingleton<ILLMProviderFactory>(provider);
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton<ILlmSessionRegistrationPort>(sessions);
        builder.Services.AddSingleton<ILlmSessionQueryPort>(sessions);
        builder.Services.AddSingleton<ResponsesObservationRuntime>();
        builder.Services.AddSingleton<ResponsesRecordingActorDispatchPort>();
        builder.Services.AddSingleton<IActorDispatchPort>(static sp => sp.GetRequiredService<ResponsesRecordingActorDispatchPort>());
        builder.Services.AddSingleton<ILlmSessionObservationScopeLeasePreparationPort>(static sp => sp.GetRequiredService<ResponsesObservationRuntime>().ScopePreparationPort);
        builder.Services.AddSingleton<ILlmSessionObservationProjectionPort>(static sp => sp.GetRequiredService<ResponsesObservationRuntime>().ProjectionPort);
        builder.Services.AddSingleton<ILlmSessionRunObservationService, LlmSessionRunObservationService>();
        builder.Services.AddSingleton<IResponsesCommandFacade, ResponsesCommandFacade>();
        builder.Services.AddSingleton<IResponsesToolClassificationService, ResponsesToolClassificationService>();
        builder.Services.AddSingleton<IResponsesDirectToolPlanService, ResponsesDirectToolPlanService>();
        builder.Services.AddSingleton<IResponsesCallerScopeResolver>(new StubResponsesCallerScopeResolver());
        builder.Services.AddToolSetRegistry(options =>
        {
            options.AddToolSet(
                ToolSetNames.WorkspaceDefault,
                [
                    _ => new StubAgentToolSource(
                    [
                        new StubAgentTool("aevatar_invoke_gagent", "Invoke a GAgent"),
                        new StubAgentTool("aevatar_invoke_team", "Invoke a team"),
                        new StubAgentTool("aevatar_start_workflow", "Start a workflow"),
                    ]),
                ]);
        });
        builder.Services.AddSingleton<IChatRoutePolicyQueryPort>(StaticChatRoutePolicyQueryPort.ForSnapshot(
            new ChatRoutePolicySnapshot(ForwardToModelAction(string.Empty), [])));
        builder.Services.AddSingleton(new ChatRouteResolver(
            new StaticChatRouteFallbackProvider(string.Empty),
            DefaultToolSetRoutingOptions()));
        builder.Services.AddSingleton<IResponsesChatRouteDecisionPort, ResponsesChatRouteDecisionPort>();
        builder.Services.AddSingleton<IResponsesRouteResolver>(new RecordingResponsesRouteResolver());

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapResponsesApiEndpoints();
        await app.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"gpt-5.4","input":"ping"}"""),
        };
        // Deliberately no Authorization header.

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(
            "authentication_required",
            "MapResponsesApiEndpoints must call .AllowAnonymous() so the handler runs and returns its structured 401 JSON, rather than being short-circuited by the host's FallbackPolicy");
        provider.LastRequest.Should().BeNull();

        await app.StopAsync();
    }

    [Fact]
    public async Task PostResponses_WhenHostAuthEnabledAndChatRouteForwardsToGAgent_UsesToolFirstPipeline()
    {
        // Companion to the AllowAnonymous test: that one proves the JwtBearer
        // FallbackPolicy doesn't short-circuit before our handler runs. This
        // one proves the POSITIVE path also threads through correctly under
        // the real AddAevatarAuthentication pipeline. Route policy pins the
        // workspace invocation tool; actor-bound direct forwarding is not part
        // of /v1/responses.
        var provider = new RecordingLLMProvider
        {
            StreamChunkBatches = ToolThenTextBatches(
                "call_auth_gagent",
                "aevatar_invoke_gagent",
                """{"payload":{"prompt":"hi via auth pipeline"}}""",
                "auth ok"),
        };
        var sessions = new RecordingResponseSessionStore();
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            GAgentToolHintAction("auth-pipeline-member"),
            []));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration["Aevatar:Authentication:Authority"] = "https://invalid.example";
        builder.Configuration["Aevatar:Authentication:Audience"] = "aevatar-api";
        builder.AddAevatarAuthentication();

        builder.Services.AddSingleton(provider);
        builder.Services.AddSingleton<ILLMProviderFactory>(provider);
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton<ILlmSessionRegistrationPort>(sessions);
        builder.Services.AddSingleton<ILlmSessionQueryPort>(sessions);
        builder.Services.AddSingleton<ResponsesObservationRuntime>();
        builder.Services.AddSingleton<ResponsesRecordingActorDispatchPort>();
        builder.Services.AddSingleton<IActorDispatchPort>(static sp => sp.GetRequiredService<ResponsesRecordingActorDispatchPort>());
        builder.Services.AddSingleton<ILlmSessionObservationScopeLeasePreparationPort>(static sp => sp.GetRequiredService<ResponsesObservationRuntime>().ScopePreparationPort);
        builder.Services.AddSingleton<ILlmSessionObservationProjectionPort>(static sp => sp.GetRequiredService<ResponsesObservationRuntime>().ProjectionPort);
        builder.Services.AddSingleton<ILlmSessionRunObservationService, LlmSessionRunObservationService>();
        builder.Services.AddSingleton<IResponsesCommandFacade, ResponsesCommandFacade>();
        builder.Services.AddSingleton<IResponsesToolClassificationService, ResponsesToolClassificationService>();
        builder.Services.AddSingleton<IResponsesDirectToolPlanService, ResponsesDirectToolPlanService>();
        builder.Services.AddSingleton<IResponsesCallerScopeResolver>(new StubResponsesCallerScopeResolver());
        builder.Services.AddToolSetRegistry(options =>
        {
            options.AddToolSet(
                ToolSetNames.WorkspaceDefault,
                [
                    _ => new StubAgentToolSource(
                    [
                        new StubAgentTool("aevatar_invoke_gagent", "Invoke a GAgent"),
                        new StubAgentTool("aevatar_invoke_team", "Invoke a team"),
                        new StubAgentTool("aevatar_start_workflow", "Start a workflow"),
                    ]),
                ]);
        });
        builder.Services.AddSingleton<IChatRoutePolicyQueryPort>(queryPort);
        builder.Services.AddSingleton(new ChatRouteResolver(
            new StaticChatRouteFallbackProvider(string.Empty),
            DefaultToolSetRoutingOptions()));
        builder.Services.AddSingleton<IResponsesChatRouteDecisionPort, ResponsesChatRouteDecisionPort>();
        builder.Services.AddSingleton<IResponsesRouteResolver>(new RecordingResponsesRouteResolver());
        builder.Services.AddSingleton<IResponsesOwnedToolCatalogPlanner>(GAgentOwnedToolCatalogPlanner());

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapResponsesApiEndpoints();
        await app.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"original-model","input":"hi via auth pipeline","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "auth-pipeline-secret");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        provider.StreamCallCount.Should().Be(1);
        var command = app.Services.GetRequiredService<ResponsesRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        var argumentsJson = ToolChoiceHintArgumentsJson(command, "aevatar_invoke_gagent");
        AssertToolArgument(argumentsJson, "actor_id", "auth-pipeline-member");
        body.Should().Contain("\"status\":\"completed\"");

        await app.StopAsync();
    }

    [Fact]
    public async Task PostResponses_WithBadPayload_ShouldReturnBadRequest()
    {
        var provider = new RecordingLLMProvider();
        await using var app = await CreateAppAsync(provider);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":" ","input":"ping"}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("model_required");
        body.Should().NotContain("secret-token");
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponses_WithoutModel_ShouldApplyIngressDefault()
    {
        // With an ingress default configured (the production default is LlmDefaults.NyxIdRouteModel),
        // a caller that omits `model` resolves to that default instead of failing model_required.
        var provider = new RecordingLLMProvider();
        await using var app = await CreateAppAsync(provider, ingressDefaultModel: LlmDefaults.NyxIdRouteModel);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"input":"ping","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain("model_required");
        provider.StreamCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetModels_WithBearer_ShouldReturnApplicationEntriesInOpenAiSpec()
    {
        var discovery = new RecordingLLMModelDiscoveryApplicationService
        {
            Entries =
            [
                new LLMModelDescriptor(
                    "claude-opus-4-7",
                    1700000000,
                    "anthropic",
                    "anthropic",
                    null,
                    null,
                    null,
                    null),
                new LLMModelDescriptor(
                    "chrono-llm/qwen-3",
                    1700000001,
                    "chrono-llm",
                    "chrono-llm",
                    null,
                    null,
                    null,
                    null),
            ],
        };
        var provider = new RecordingLLMProvider();
        await using var app = await CreateAppAsync(
            provider,
            callerScopeResolver: new StubResponsesCallerScopeResolver(scopeId: "scope-1"),
            modelDiscoveryService: discovery);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "models-token");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("object").GetString().Should().Be("list");
        var data = doc.RootElement.GetProperty("data");
        data.GetArrayLength().Should().Be(2);
        data[0].GetProperty("id").GetString().Should().Be("claude-opus-4-7");
        data[0].GetProperty("object").GetString().Should().Be("model");
        data[0].GetProperty("owned_by").GetString().Should().Be("anthropic");
        data[0].GetProperty("group").GetString().Should().Be("anthropic");
        data[0].TryGetProperty("route_value", out _).Should().BeFalse();
        data[1].GetProperty("id").GetString().Should().Be("chrono-llm/qwen-3");
        data[1].GetProperty("group").GetString().Should().Be("chrono-llm");
        discovery.LastScopeId.Should().Be("scope-1");
        discovery.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetModels_WithoutBearer_ShouldReturnStructured401()
    {
        var discovery = new RecordingLLMModelDiscoveryApplicationService();
        var provider = new RecordingLLMProvider();
        await using var app = await CreateAppAsync(provider, modelDiscoveryService: discovery);

        var response = await app.GetTestClient().GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("authentication_required");
        discovery.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetModels_WhenNyxIdRejectsCallerAuthentication_ShouldReturnStructured401()
    {
        var discovery = new RecordingLLMModelDiscoveryApplicationService
        {
            Exception = new LLMModelCatalogApplicationException(
                LLMModelCatalogApplicationErrorKind.AuthenticationRejected,
                "NYXID_AUTHENTICATION_REJECTED",
                "caller token expired"),
        };
        var provider = new RecordingLLMProvider();
        await using var app = await CreateAppAsync(
            provider,
            callerScopeResolver: new StubResponsesCallerScopeResolver(scopeId: "scope-1"),
            modelDiscoveryService: discovery);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "models-token");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("authentication_failed");
    }

    [Fact]
    public async Task PostResponses_WithProxyServicePrefix_ShouldStripAndResolveToProxyPlaneRoute()
    {
        // Stage 2: client picks `chrono-llm/qwen-3` from the catalog. The create handler must
        // call the route resolver to recover chrono-llm's full RouteValue
        // (/api/v1/proxy/s/chrono-llm) and pass the bare model name `qwen-3` to the provider.
        // Response-snapshot echo preserves the original prefixed model so the client sees
        // back what it sent.
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk
                {
                    DeltaContent = "ok",
                    IsLast = true,
                    Usage = new TokenUsage(1, 1, 2),
                },
            ],
        };
        await using var app = await CreateAppAsync(provider);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""
            {
              "model": "chrono-llm/qwen-3",
              "input": "ping",
              "stream": false
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "vendor-secret");
        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Model.Should().Be("qwen-3");
        provider.LastRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdRoutePreference);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("model").GetString().Should().Be("chrono-llm/qwen-3");
    }

    [Fact]
    public async Task PostResponses_WithGatewayProviderPrefix_ShouldResolveToGatewayPlaneRoute()
    {
        // Stage 2 (OpenRouter-style consistent prefix): client picks
        // `anthropic/claude-opus-4-7`. The resolver returns /api/v1/llm/anthropic/v1
        // (NOT /api/v1/proxy/s/anthropic — anthropic isn't a proxy slug; it's a gateway
        // provider whose per-provider plane is reached via /api/v1/llm/<slug>/v1).
        // Without catalog lookup, naive slug-direct routing would 404 here.
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "ok", IsLast = true, Usage = new TokenUsage(1, 1, 2) },
            ],
        };
        await using var app = await CreateAppAsync(provider);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"anthropic/claude-opus-4-7","input":"ping","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "gateway-prefix-secret");
        var response = await app.GetTestClient().SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        provider.LastRequest!.Model.Should().Be("claude-opus-4-7");
        provider.LastRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdRoutePreference);
    }

    [Fact]
    public async Task PostResponses_WithFunctionCallOutputAndNoPreviousResponseId_ShouldFoldIntoPromptAsContext()
    {
        // CC Switch / Codex translating Claude Code's prior tool-result turn into a fresh
        // `/v1/responses` call carries `function_call_output` items in `input` but does NOT
        // propagate `previous_response_id` (they don't model OpenAI's server-side session).
        // Strict normalization would 400 with `function_call_output requires previous_response_id`;
        // instead the normalizer folds the tool result into the user prompt as historical
        // context so multi-turn tool conversations work without the continuation contract.
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "ok", IsLast = true, Usage = new TokenUsage(1, 1, 2) },
            ],
        };
        await using var app = await CreateAppAsync(provider);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""
            {
              "model": "gpt-5.4",
              "input": [
                {"type": "input_text", "text": "what services do I have on nyxid?"},
                {"type": "function_call_output", "call_id": "call_1", "output": "{\"services\":[\"chrono-llm\",\"llm-anthropic\"]}"}
              ],
              "stream": false
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "fold-secret");
        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        provider.LastRequest.Should().NotBeNull();
        // Tool result is folded into the prompt as a `[tool_result call_id=…]` marker.
        var userMessage = provider.LastRequest!.Messages.Single(m => m.Role == "user");
        userMessage.Content.Should().Contain("what services do I have on nyxid?");
        userMessage.Content.Should().Contain("[tool_result call_id=call_1]");
        userMessage.Content.Should().Contain("chrono-llm");
    }

    [Fact]
    public async Task PostResponses_WithBuiltInToolDeclarations_ShouldSkipNonFunctionTypesAndAcceptFunctionTypes()
    {
        // OpenAI Responses API permits built-in tool declarations alongside function
        // tools. CC Switch / Codex / Cursor will pass these through when proxying
        // Claude Code's tool list onto an OpenAI-compatible endpoint. The normalizer
        // must:
        //   - silently skip built-in tool entries (no name, type ≠ "function")
        //   - still validate name for function-type entries
        //   - admit the request (no `invalid_tools` 400) so the LLM gets to see the
        //     remaining function tools.
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "ok", IsLast = true, Usage = new TokenUsage(1, 1, 2) },
            ],
        };
        await using var app = await CreateAppAsync(provider);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""
            {
              "model": "chrono-llm/gpt-5.5",
              "input": "ping",
              "stream": false,
              "tools": [
                {"type": "web_search_preview"},
                {"type": "file_search", "vector_store_ids": ["vs_abc"]},
                {"type": "function", "name": "Bash", "description": "Run shell", "parameters": {"type":"object","properties":{}}}
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tools-secret");
        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        // Built-ins are dropped; the caller-declared function remains forwarded while the
        // unprofiled endpoint does not auto-union route tools.
        provider.LastRequest!.Tools.Should().NotBeNull();
        provider.LastRequest.Tools!.Select(static tool => tool.Name)
            .Should().Equal("Bash");
    }

    [Fact]
    public async Task PostResponses_WithNonObjectToolEntries_ShouldSkipThemAndAdmitRequest()
    {
        // Some OpenAI-compatible clients emit non-object entries in `tools` (null
        // padding, stray strings) alongside real tool objects. aevatar only owns
        // function-typed tool declarations, so a non-object entry — exactly like a
        // built-in tool object it doesn't own — must be passed over and the request
        // admitted, never failing the whole request. In particular a bare greeting that
        // needs no tools must still succeed even when the client ships a malformed entry.
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "ok", IsLast = true, Usage = new TokenUsage(1, 1, 2) },
            ],
        };
        await using var app = await CreateAppAsync(provider);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""
            {
              "model": "chrono-llm/gpt-5.5",
              "input": "你好",
              "stream": false,
              "tools": [
                "not_an_object",
                null,
                {"type": "function", "name": "Bash", "description": "Run shell", "parameters": {"type":"object","properties":{}}}
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tools-secret");
        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        // Non-object entries are dropped; the function-typed tool still reaches the LLM provider.
        provider.LastRequest!.Tools.Should().NotBeNull();
        provider.LastRequest.Tools!.Select(static tool => tool.Name).Should().Contain("Bash");
    }

    [Fact]
    public async Task PostResponses_WithFunctionToolMissingName_ShouldStillReturnIndexedInvalidTools()
    {
        // Built-ins are accepted because they have a non-function type. A function-typed
        // tool WITHOUT a name is still malformed and must 400 with an actionable error
        // that names the offending index.
        var provider = new RecordingLLMProvider();
        await using var app = await CreateAppAsync(provider);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""
            {
              "model": "gpt-5.4",
              "input": "ping",
              "tools": [
                {"type": "web_search_preview"},
                {"type": "function", "description": "missing name field"}
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "bad-tools-secret");
        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("invalid_tools");
        body.Should().Contain("function tool at index 1");
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponses_WithUnknownVendorPrefix_ShouldFailClosed()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "ok", IsLast = true, Usage = new TokenUsage(1, 1, 2) },
            ],
        };
        var emptyResolver = new RecordingResponsesRouteResolver();
        await using var app = await CreateAppAsync(provider, routeResolver: emptyResolver);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"mistralai/mistral-7b","input":"ping","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unknown-vendor-secret");
        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
        body.Should().Contain("model_not_found");
        emptyResolver.CallCount.Should().Be(1);
        emptyResolver.LastSlug.Should().Be("mistralai");
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponses_WithBareModel_ShouldNotSetRoutePreference()
    {
        // Back-compat: gateway-routed models stay bare. No prefix → no route preference.
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "ok", IsLast = true, Usage = new TokenUsage(1, 1, 2) },
            ],
        };
        await using var app = await CreateAppAsync(provider);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"gpt-5.4","input":"ping","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "bare-secret");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        provider.LastRequest!.Model.Should().Be("gpt-5.4");
        provider.LastRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdRoutePreference);
    }

    [Fact]
    public async Task PostResponses_WhenChatRouteForwardsToModel_RewritesModelBeforeCompletionService()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "routed", IsLast = true, Usage = new TokenUsage(1, 1, 2) },
            ],
        };
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            ForwardToModelAction("routed-model"),
            []));
        await using var app = await CreateAppAsync(provider, chatRoutePolicyQueryPort: queryPort);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"original-model","input":"ping","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "route-secret");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Model.Should().Be("routed-model");
    }

    [Fact]
    public async Task PostResponses_WhenChatRouteMatchesModelAndDeclaredTools_UsesRuleAction()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "routed", IsLast = true, Usage = new TokenUsage(1, 1, 2) },
            ],
        };
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "model-tools",
                    Priority = 10,
                    Match = new ChatRouteMatch
                    {
                        SourceKind = ChatSourceKind.NyxResponses,
                        Model = "original-model",
                        ToolMode = ToolMode.Declared,
                    },
                    Action = ForwardToModelAction("routed-tool-model"),
                },
            ]));
        await using var app = await CreateAppAsync(provider, chatRoutePolicyQueryPort: queryPort);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""
            {
              "model":"original-model",
              "input":"ping",
              "stream":false,
              "tools":[{"type":"function","name":"do_thing","parameters":{"type":"object","properties":{}}}]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "route-secret");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Model.Should().Be("routed-tool-model");
        provider.LastRequest.Tools.Should().NotBeNull();
        provider.LastRequest.Tools!.Select(static tool => tool.Name)
            .Should().Equal("do_thing");
    }

    [Fact]
    public async Task PostResponses_WhenChatRouteRejects_ReturnsForbiddenWithoutLlmCall()
    {
        var provider = new RecordingLLMProvider();
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            RejectAction("policy_denied", "blocked by policy"),
            []));
        await using var app = await CreateAppAsync(provider, chatRoutePolicyQueryPort: queryPort);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"original-model","input":"ping","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "route-secret");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("chat_route_rejected");
        doc.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Be("blocked by policy");
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponses_WhenChatRouteForwardsToGAgent_UsesWorkspaceInvokeTool()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunkBatches = ToolThenTextBatches(
                "call_gagent_1",
                "aevatar_invoke_gagent",
                """{"payload":{"prompt":"hi gagent"}}""",
                "hello agent"),
        };
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            GAgentToolHintAction("member-7"),
            []));
        var responseSessions = new RecordingResponseSessionStore();
        await using var app = await CreateAppAsync(
            provider,
            responseSessions,
            chatRoutePolicyQueryPort: queryPort,
            ownedToolCatalogPlanner: GAgentOwnedToolCatalogPlanner());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"original-model","input":"hi gagent","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "gagent-secret");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        responseSessions.Registered.Should().ContainSingle();
        provider.StreamCallCount.Should().Be(1);
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Tools.Should().NotBeNull();
        provider.LastRequest.Tools!.Select(static tool => tool.Name)
            .Should().Contain("aevatar_invoke_gagent");
        var command = app.Services.GetRequiredService<ResponsesRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        var argumentsJson = ToolChoiceHintArgumentsJson(command, "aevatar_invoke_gagent");
        AssertToolArgument(argumentsJson, "actor_id", "member-7");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("completed");
        AssertCompletedMessage(root, string.Empty);
        root.GetProperty("output").GetArrayLength().Should().Be(1);
        body.Should().NotContain("\"type\":\"function_call\"");
        body.Should().NotContain("call_gagent_1");
    }

    [Fact]
    public async Task PostResponses_StreamWhenChatRouteForwardsToGAgent_StreamsToolFirstResponse()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunkBatches = ToolThenTextBatches(
                "call_gagent_stream",
                "aevatar_invoke_gagent",
                "{}",
                "alpha beta"),
        };
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            GAgentToolHintAction("member-stream"),
            []));
        var responseSessions = new RecordingResponseSessionStore();
        await using var app = await CreateAppAsync(
            provider,
            responseSessions,
            chatRoutePolicyQueryPort: queryPort,
            ownedToolCatalogPlanner: GAgentOwnedToolCatalogPlanner());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"original-model","input":"stream me","stream":true}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "gagent-stream-secret");

        var response = await app.GetTestClient().SendAsync(request, HttpCompletionOption.ResponseContentRead);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        body.Should().Contain("event: response.created");
        body.Should().NotContain("event: response.output_text.delta");
        body.Should().Contain("event: response.output_text.done");
        body.Should().Contain("\"text\":\"\"");
        body.Should().NotContain("\"type\":\"function_call\"");
        body.Should().NotContain("call_gagent_stream");
        body.Should().Contain("event: response.completed");
        var command = app.Services.GetRequiredService<ResponsesRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        var argumentsJson = ToolChoiceHintArgumentsJson(command, "aevatar_invoke_gagent");
        AssertToolArgument(argumentsJson, "actor_id", "member-stream");
        responseSessions.RecordedCompletions.Should().BeEmpty();
    }

    [Fact]
    public async Task PostResponses_StreamWhenChatRouteForwardToolArgumentsConflict_ReturnsToolErrorToSecondRound()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunkBatches =
            [
                [
                    new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = "call_gagent_conflict",
                            Name = "aevatar_invoke_gagent",
                            ArgumentsJson = """{"actor_id":"model-chosen"}""",
                        },
                        IsLast = true,
                    },
                ],
                [new LLMStreamChunk { DeltaContent = "handled", IsLast = true }],
            ],
        };
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            GAgentToolHintAction("member-trusted"),
            []));
        var responseSessions = new RecordingResponseSessionStore();
        await using var app = await CreateAppAsync(
            provider,
            responseSessions,
            chatRoutePolicyQueryPort: queryPort,
            ownedToolCatalogPlanner: GAgentOwnedToolCatalogPlanner());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"original-model","input":"stream me","stream":true}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "gagent-tool-stream-secret");

        var response = await app.GetTestClient().SendAsync(request, HttpCompletionOption.ResponseContentRead);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        body.Should().Contain("event: response.completed");
        responseSessions.ForwardedToolCalls.Should().BeEmpty();
        body.Should().NotContain("\"type\":\"function_call\"");
        body.Should().NotContain("call_gagent_conflict");
    }

    [Fact]
    public async Task PostResponses_WhenChatRouteForwardsToGAgentWithToolCall_AppliesPrefilledActor()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunkBatches = ToolThenTextBatches(
                "call_gagent_sync",
                "aevatar_invoke_gagent",
                """{"payload":{"prompt":"sync me"}}""",
                "done"),
        };
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            GAgentToolHintAction("member-sync"),
            []));
        var responseSessions = new RecordingResponseSessionStore();
        await using var app = await CreateAppAsync(
            provider,
            responseSessions,
            chatRoutePolicyQueryPort: queryPort,
            ownedToolCatalogPlanner: GAgentOwnedToolCatalogPlanner());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"original-model","input":"sync me","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "gagent-sync-secret");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\"status\":\"completed\"");
        responseSessions.ForwardedToolCalls.Should().BeEmpty();
        var command = app.Services.GetRequiredService<ResponsesRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        var argumentsJson = ToolChoiceHintArgumentsJson(command, "aevatar_invoke_gagent");
        AssertToolArgument(argumentsJson, "actor_id", "member-sync");
    }

    [Fact]
    public async Task PostResponses_WhenChatRouteForwardsToGAgentWithEmptyActorId_PrefillsEmptyActorId()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunkBatches = ToolThenTextBatches(
                "call_gagent_empty",
                "aevatar_invoke_gagent",
                "{}",
                "done"),
        };
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            GAgentToolHintAction(string.Empty),
            []));
        var responseSessions = new RecordingResponseSessionStore();
        await using var app = await CreateAppAsync(
            provider,
            responseSessions,
            chatRoutePolicyQueryPort: queryPort,
            ownedToolCatalogPlanner: GAgentOwnedToolCatalogPlanner());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"original-model","input":"ping","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "gagent-empty-secret");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        responseSessions.ForwardedToolCalls.Should().BeEmpty();
        var command = app.Services.GetRequiredService<ResponsesRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        var argumentsJson = ToolChoiceHintArgumentsJson(command, "aevatar_invoke_gagent");
        AssertToolArgument(argumentsJson, "actor_id", string.Empty);
    }

    [Fact]
    public async Task PostResponses_WhenGAgentToolTargetDoesNotExist_DoesNotResolveDuringRoute()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunkBatches = ToolThenTextBatches(
                "call_ghost_gagent",
                "aevatar_invoke_gagent",
                "{}",
                "queued"),
        };
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            GAgentToolHintAction("ghost-member"),
            []));
        var responseSessions = new RecordingResponseSessionStore();
        await using var app = await CreateAppAsync(
            provider,
            responseSessions,
            chatRoutePolicyQueryPort: queryPort,
            ownedToolCatalogPlanner: GAgentOwnedToolCatalogPlanner());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"original-model","input":"ping","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "gagent-ghost-secret");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var command = app.Services.GetRequiredService<ResponsesRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        ToolChoiceHintArgumentsJson(command, "aevatar_invoke_gagent")
            .Should().Contain("\"actor_id\":\"ghost-member\"");
    }

    [Fact]
    public async Task PostResponses_StreamWhenGAgentToolTargetDoesNotExist_DoesNotResolveDuringRoute()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunkBatches = ToolThenTextBatches(
                "call_ghost_gagent_stream",
                "aevatar_invoke_gagent",
                "{}",
                "queued"),
        };
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            GAgentToolHintAction("ghost-member"),
            []));
        var responseSessions = new RecordingResponseSessionStore();
        await using var app = await CreateAppAsync(
            provider,
            responseSessions,
            chatRoutePolicyQueryPort: queryPort,
            ownedToolCatalogPlanner: GAgentOwnedToolCatalogPlanner());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"original-model","input":"ping","stream":true}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "gagent-ghost-stream-secret");

        var response = await app.GetTestClient().SendAsync(request, HttpCompletionOption.ResponseContentRead);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        body.Should().Contain("event: response.created");
        body.Should().Contain("event: response.completed");
        body.Should().NotContain("event: response.failed");
        var command = app.Services.GetRequiredService<ResponsesRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        ToolChoiceHintArgumentsJson(command, "aevatar_invoke_gagent")
            .Should().Contain("\"actor_id\":\"ghost-member\"");
    }

    [Fact]
    public async Task PostResponses_WhenGAgentActorIdWouldBreakDirectResolver_DoesNotUseDirectResolver()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunkBatches = ToolThenTextBatches(
                "call_bad_gagent",
                "aevatar_invoke_gagent",
                "{}",
                "queued"),
        };
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            GAgentToolHintAction("bad/member"),
            []));
        var responseSessions = new RecordingResponseSessionStore();
        await using var app = await CreateAppAsync(
            provider,
            responseSessions,
            chatRoutePolicyQueryPort: queryPort,
            ownedToolCatalogPlanner: GAgentOwnedToolCatalogPlanner());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"original-model","input":"ping","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "gagent-bad-secret");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var command = app.Services.GetRequiredService<ResponsesRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        ToolChoiceHintArgumentsJson(command, "aevatar_invoke_gagent")
            .Should().Contain("\"actor_id\":\"bad/member\"");
    }

    [Fact]
    public async Task PostResponses_WhenChatRouteForwardsToTeam_UsesWorkspaceInvokeTool()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunkBatches = ToolThenTextBatches(
                "call_team_1",
                "aevatar_invoke_team",
                """{"payload":{"prompt":"hi team"}}""",
                "hello world"),
        };
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            TeamToolHintAction("team-1", "chat"),
            []));
        var responseSessions = new RecordingResponseSessionStore();
        await using var app = await CreateAppAsync(
            provider,
            responseSessions,
            chatRoutePolicyQueryPort: queryPort,
            ownedToolCatalogPlanner: TeamOwnedToolCatalogPlanner());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"original-model","input":"hi team","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "team-secret");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        responseSessions.Registered.Should().ContainSingle();
        provider.StreamCallCount.Should().Be(1);
        var command = app.Services.GetRequiredService<ResponsesRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        var argumentsJson = ToolChoiceHintArgumentsJson(command, "aevatar_invoke_team");
        AssertToolArgument(argumentsJson, "team_id", "team-1");
        AssertToolArgument(argumentsJson, "endpoint_id", "chat");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("completed");
        AssertCompletedMessage(root, string.Empty);
        root.GetProperty("output").GetArrayLength().Should().Be(1);
        body.Should().NotContain("\"type\":\"function_call\"");
        body.Should().NotContain("call_team_1");
    }

    [Fact]
    public async Task PostResponses_StreamWhenChatRouteForwardsToTeam_StreamsToolFirstResponse()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunkBatches = ToolThenTextBatches(
                "call_team_stream",
                "aevatar_invoke_team",
                "{}",
                "alpha beta"),
        };
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            TeamToolHintAction("team-2", "chat"),
            []));
        var responseSessions = new RecordingResponseSessionStore();
        await using var app = await CreateAppAsync(
            provider,
            responseSessions,
            chatRoutePolicyQueryPort: queryPort,
            ownedToolCatalogPlanner: TeamOwnedToolCatalogPlanner());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"original-model","input":"stream me","stream":true}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "team-stream-secret");

        var response = await app.GetTestClient().SendAsync(request, HttpCompletionOption.ResponseContentRead);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        body.Should().Contain("event: response.created");
        body.Should().NotContain("event: response.output_text.delta");
        body.Should().Contain("event: response.output_text.done");
        body.Should().Contain("\"text\":\"\"");
        body.Should().NotContain("\"type\":\"function_call\"");
        body.Should().NotContain("call_team_stream");
        body.Should().Contain("event: response.completed");
        var command = app.Services.GetRequiredService<ResponsesRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        var argumentsJson = ToolChoiceHintArgumentsJson(command, "aevatar_invoke_team");
        AssertToolArgument(argumentsJson, "team_id", "team-2");
        AssertToolArgument(argumentsJson, "endpoint_id", "chat");
        responseSessions.RecordedCompletions.Should().BeEmpty();
    }

    [Fact]
    public async Task PostResponses_WhenChatRouteForwardsToUnknownTeam_DoesNotResolveDuringRoute()
    {
        var provider = new RecordingLLMProvider
        {
            StreamChunkBatches = ToolThenTextBatches(
                "call_missing_team",
                "aevatar_invoke_team",
                "{}",
                "queued"),
        };
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            TeamToolHintAction("missing-team", "chat"),
            []));
        var responseSessions = new RecordingResponseSessionStore();
        await using var app = await CreateAppAsync(
            provider,
            responseSessions,
            chatRoutePolicyQueryPort: queryPort,
            ownedToolCatalogPlanner: TeamOwnedToolCatalogPlanner());

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"original-model","input":"hi","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "team-secret");

        var response = await app.GetTestClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var command = app.Services.GetRequiredService<ResponsesRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        var argumentsJson = ToolChoiceHintArgumentsJson(command, "aevatar_invoke_team");
        AssertToolArgument(argumentsJson, "team_id", "missing-team");
        AssertToolArgument(argumentsJson, "endpoint_id", "chat");
    }

    [Fact]
    public async Task PostResponses_WithNonSlugLookingPrefix_ShouldPassModelVerbatim()
    {
        // Edge case: `BadSlug/x` has an uppercase prefix that doesn't match the slug pattern
        // (lowercase + digits + hyphens, length 2-64). The parser must NOT consume it as a
        // route — pass through unchanged so the LLM provider gets the original model string.
        var provider = new RecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "ok", IsLast = true, Usage = new TokenUsage(1, 1, 2) },
            ],
        };
        await using var app = await CreateAppAsync(provider);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = JsonContent("""{"model":"BadSlug/gpt-x","input":"ping","stream":false}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "edge-secret");
        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        provider.LastRequest!.Model.Should().Be("BadSlug/gpt-x");
        provider.LastRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdRoutePreference);
    }

    private static async Task<WebApplication> CreateAppAsync(
        RecordingLLMProvider provider,
        RecordingResponseSessionStore? responseSessions = null,
        IResponsesCallerScopeResolver? callerScopeResolver = null,
        IResponsesToolProvider? responsesToolProvider = null,
        ILLMModelDiscoveryApplicationService? modelDiscoveryService = null,
        IResponsesRouteResolver? routeResolver = null,
        IChatRoutePolicyQueryPort? chatRoutePolicyQueryPort = null,
        ILlmSessionRunObservationService? observationService = null,
        string? ingressDefaultModel = null,
        IResponsesOwnedToolCatalogPlanner? ownedToolCatalogPlanner = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        // Tests opt out of the production ingress default (LlmDefaults.NyxIdRouteModel) by default
        // so the "model is required" contract stays exercised; pass ingressDefaultModel to cover
        // the default-applied path.
        builder.Services.Configure<ResponsesIngressOptions>(o => o.DefaultModel = ingressDefaultModel);
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(provider);
        builder.Services.AddSingleton<ILLMProviderFactory>(provider);
        responseSessions ??= new RecordingResponseSessionStore();
        builder.Services.AddSingleton(responseSessions);
        builder.Services.AddSingleton<ILlmSessionRegistrationPort>(responseSessions);
        builder.Services.AddSingleton<ILlmSessionQueryPort>(responseSessions);
        builder.Services.AddSingleton<ResponsesObservationRuntime>();
        builder.Services.AddSingleton<ResponsesRecordingActorDispatchPort>();
        builder.Services.AddSingleton<IActorDispatchPort>(static sp => sp.GetRequiredService<ResponsesRecordingActorDispatchPort>());
        builder.Services.AddSingleton<ILlmSessionObservationScopeLeasePreparationPort>(static sp => sp.GetRequiredService<ResponsesObservationRuntime>().ScopePreparationPort);
        builder.Services.AddSingleton<ILlmSessionObservationProjectionPort>(static sp => sp.GetRequiredService<ResponsesObservationRuntime>().ProjectionPort);
        if (observationService is null)
            builder.Services.AddSingleton<ILlmSessionRunObservationService, LlmSessionRunObservationService>();
        else
            builder.Services.AddSingleton(observationService);
        builder.Services.AddSingleton<IResponsesCommandFacade, ResponsesCommandFacade>();
        builder.Services.AddSingleton<IResponsesToolClassificationService, ResponsesToolClassificationService>();
        builder.Services.AddSingleton<IResponsesDirectToolPlanService, ResponsesDirectToolPlanService>();
        builder.Services.AddSingleton(callerScopeResolver ?? new StubResponsesCallerScopeResolver());
        builder.Services.AddToolSetRegistry(options =>
        {
            options.AddToolSet(
                ToolSetNames.WorkspaceDefault,
                [
                    _ => new StubAgentToolSource(
                    [
                        new StubAgentTool("aevatar_invoke_gagent", "Invoke a GAgent"),
                        new StubAgentTool("aevatar_invoke_team", "Invoke a team"),
                        new StubAgentTool("aevatar_start_workflow", "Start a workflow"),
                    ]),
                ]);
        });
        builder.Services.AddSingleton(chatRoutePolicyQueryPort ?? StaticChatRoutePolicyQueryPort.ForSnapshot(
            new ChatRoutePolicySnapshot(ForwardToModelAction(string.Empty), [])));
        builder.Services.AddSingleton(new ChatRouteResolver(
            new StaticChatRouteFallbackProvider(string.Empty),
            DefaultToolSetRoutingOptions()));
        builder.Services.AddSingleton<IResponsesChatRouteDecisionPort, ResponsesChatRouteDecisionPort>();
        builder.Services.AddSingleton<ILLMModelDiscoveryApplicationService>(
            modelDiscoveryService ?? new RecordingLLMModelDiscoveryApplicationService());
        builder.Services.AddSingleton(routeResolver ?? new RecordingResponsesRouteResolver
        {
            Routes =
            {
                // Default test catalog: chrono-llm is a proxy-plane service,
                // anthropic is a gateway-plane service. Other tests use this
                // unless they pass their own RecordingResponsesRouteResolver.
                ["chrono-llm"] = UserTarget("user-chrono-llm", "chrono-llm"),
                ["chrono-llm-public"] = UserTarget("user-chrono-llm-public", "chrono-llm-public"),
                ["anthropic"] = CatalogTarget("catalog-anthropic", "anthropic"),
            },
        });
        if (responsesToolProvider != null)
            builder.Services.AddSingleton(responsesToolProvider);
        if (ownedToolCatalogPlanner != null)
            builder.Services.AddSingleton(ownedToolCatalogPlanner);

        var app = builder.Build();
        app.MapResponsesApiEndpoints();
        await app.StartAsync();
        return app;
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static LLMRouteTarget CatalogTarget(string catalogServiceId, string serviceSlug) => new()
    {
        CatalogServiceId = catalogServiceId,
        ServiceSlugSnapshot = serviceSlug,
    };

    private static LLMRouteTarget UserTarget(string userServiceId, string serviceSlug) => new()
    {
        UserServiceId = userServiceId,
        ServiceSlugSnapshot = serviceSlug,
    };

    private sealed class ResponsesRecordingActorDispatchPort(
        RecordingLLMProvider provider,
        ResponsesObservationRuntime observationRuntime) : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public async Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope));
            if (envelope.Payload?.Is(LlmRunRequested.Descriptor) == true)
            {
                var command = envelope.Payload.Unpack<LlmRunRequested>();
                provider.CaptureDispatchedCommand(command);
                await observationRuntime.PublishFromProviderAsync(command, provider, ct);
            }

            return DispatchAdmissionFactory.Create(actorId, envelope);
        }
    }

    private sealed class StaticLlmSessionRunObservationService(LlmSessionRunObservedResult result)
        : ILlmSessionRunObservationService
    {
        public static StaticLlmSessionRunObservationService Error(
            LlmSessionRunObservedTerminalKind kind,
            int statusCode,
            string code,
            string message) =>
            new(new LlmSessionRunObservedResult(
                null,
                null,
                new LlmSessionRunObservedError(kind, statusCode, code, message)));

        public async Task<LlmSessionRunObservedResult> ObserveAsync(
            LlmSessionRunObservationRequest request,
            Func<LlmSessionRunObservedDelta, CancellationToken, ValueTask>? onDelta,
            CancellationToken ct = default)
        {
            var admission = await request.DispatchAsync(ct);
            return result with { Admission = admission };
        }
    }

    private sealed class ResponsesObservationRuntime
    {
        public ResponsesObservationRuntime()
        {
            ScopePreparationPort = new ResponsesObservationScopeLeasePreparationPort();
            ProjectionPort = new ResponsesObservationProjectionPort();
        }

        public ResponsesObservationScopeLeasePreparationPort ScopePreparationPort { get; }

        public ResponsesObservationProjectionPort ProjectionPort { get; }

        public async Task PublishFromProviderAsync(
            LlmRunRequested command,
            RecordingLLMProvider provider,
            CancellationToken ct)
        {
            if (ProjectionPort.Sink is null)
                return;

            var chunks = await provider.CollectForCommandAsync(command, ct);
            var outputText = new StringBuilder();
            TokenUsage? usage = null;
            var forwardedToolCalls = new List<LlmSessionRuntimeToolCall>();
            var forwardedToolNames = command.ToolSelection?.ForwardedTools
                .Select(static tool => tool.ToolName)
                .Except(command.ToolSelection.SubstitutedToolNames, StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal) ?? [];

            foreach (var chunk in chunks)
            {
                if (!string.IsNullOrEmpty(chunk.DeltaContent))
                {
                    outputText.Append(chunk.DeltaContent);
                    ProjectionPort.Sink.Push(ObservedEnvelope(new LlmStreamChunkObserved
                    {
                        ResponseId = command.ResponseId,
                        RunId = command.RunId,
                        DeltaText = chunk.DeltaContent,
                        ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    }));
                }

                if (chunk.DeltaToolCall is not null)
                {
                    var runtimeCall = ToRuntimeToolCall(chunk.DeltaToolCall);
                    if (forwardedToolNames.Contains(runtimeCall.ToolName))
                        forwardedToolCalls.Add(runtimeCall.Clone());
                    ProjectionPort.Sink.Push(ObservedEnvelope(new LlmStreamChunkObserved
                    {
                        ResponseId = command.ResponseId,
                        RunId = command.RunId,
                        ToolCallDelta = runtimeCall,
                        ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    }));
                }

                if (chunk.Usage is not null)
                    usage = chunk.Usage;
            }

            var completed = new LlmRunCompleted
            {
                ResponseId = command.ResponseId,
                RunId = command.RunId,
                OutputText = outputText.ToString(),
                CompletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            };
            completed.ForwardedToolCalls.AddRange(forwardedToolCalls);
            if (usage is not null)
            {
                completed.Usage = new LlmSessionTokenUsage
                {
                    PromptTokens = usage.PromptTokens,
                    CompletionTokens = usage.CompletionTokens,
                    TotalTokens = usage.TotalTokens,
                };
            }

            ProjectionPort.Sink.Push(ObservedEnvelope(completed));
        }

        private static EventEnvelope ObservedEnvelope(Google.Protobuf.IMessage payload) =>
            new()
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Google.Protobuf.WellKnownTypes.Any.Pack(payload),
            };

        private static LlmSessionRuntimeToolCall ToRuntimeToolCall(ToolCall call) =>
            new()
            {
                CallId = call.Id,
                ToolName = call.Name,
                ArgumentsJson = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson,
                Arguments = ParseBoundaryObject(call.ArgumentsJson),
            };

        private static Struct ParseBoundaryObject(string? json)
        {
            var value = ResponsesJsonValues.ParseBoundaryPayload(json);
            return value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StructValue
                ? value.StructValue.Clone()
                : new Struct();
        }
    }

    private sealed class ResponsesObservationScopeLeasePreparationPort
        : ILlmSessionObservationScopeLeasePreparationPort
    {
        public Task<LlmSessionObservationScopeLeasePreparation?> PrepareAsync(
            string actorId,
            string responseId,
            CancellationToken ct = default) =>
            Task.FromResult<LlmSessionObservationScopeLeasePreparation?>(
                new LlmSessionObservationScopeLeasePreparation(actorId, responseId));

        public Task ReleaseAsync(
            LlmSessionObservationScopeLeasePreparation preparation,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class ResponsesObservationProjectionPort : ILlmSessionObservationProjectionPort
    {
        public IEventSink<EventEnvelope>? Sink { get; private set; }

        public bool ProjectionEnabled => true;

        public Task<EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>?> AttachExistingResponseProjectionAsync(
            string actorId,
            string responseId,
            IEventSink<EventEnvelope> sink,
            CancellationToken ct = default)
        {
            Sink = sink;
            return Task.FromResult<EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>?>(
                new EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>(
                    new ResponsesObservationLease(actorId, responseId),
                    new NoOpAsyncDisposable()));
        }

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            ILlmSessionObservationProjectionLease lease,
            IEventSink<EventEnvelope> sink,
            CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable?>(new NoOpAsyncDisposable());

        public Task DetachLiveSinkAsync(IAsyncDisposable? liveSinkLease, CancellationToken ct = default)
        {
            Sink = null;
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            ILlmSessionObservationProjectionLease lease,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed record ResponsesObservationLease(string ActorId, string ResponseId)
        : ILlmSessionObservationProjectionLease;

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Microsoft.Extensions.Options.IOptions<ChatRoutingOptions> DefaultToolSetRoutingOptions() =>
        Microsoft.Extensions.Options.Options.Create(new ChatRoutingOptions
        {
            Defaults = new ChatRoutingDefaultsOptions
            {
                DefaultForwardToModelToolSetName = ToolSetNames.WorkspaceDefault,
            },
        });

    private static string? GetErrorCode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private static void AssertCompletedMessage(JsonElement response, string expectedText)
    {
        var output = response.GetProperty("output");
        output.GetArrayLength().Should().BeGreaterThan(0);
        output[0].GetProperty("status").GetString().Should().Be("completed");
        if (string.IsNullOrWhiteSpace(expectedText))
        {
            output[0].GetProperty("content").GetArrayLength().Should().Be(0);
            return;
        }

        output[0].GetProperty("content")[0].GetProperty("text").GetString().Should().Be(expectedText);
    }

    private static IReadOnlyList<IReadOnlyList<LLMStreamChunk>> ToolThenTextBatches(
        string callId,
        string toolName,
        string argumentsJson,
        string finalText) =>
        [
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = callId,
                        Name = toolName,
                        ArgumentsJson = argumentsJson,
                    },
                    IsLast = true,
                },
            ],
            [new LLMStreamChunk { DeltaContent = finalText, IsLast = true }],
        ];

    private static string RouteToolArgumentsJson(LLMRequest request, string toolName)
    {
        request.Messages.Should().HaveCountGreaterThanOrEqualTo(2);
        var toolCall = request.Messages[1].ToolCalls.Should().ContainSingle().Subject;
        toolCall.Name.Should().Be(toolName);
        return toolCall.ArgumentsJson;
    }

    private static string ToolChoiceHintArgumentsJson(LlmRunRequested command, string toolName)
    {
        command.ToolSelection.ToolChoiceHintName.Should().Be(toolName);
        return command.ToolSelection.ToolChoiceHintArgumentsJson;
    }

    private static void AssertToolArgument(string argumentsJson, string path, string expected)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var current = doc.RootElement;
        foreach (var segment in path.Split('.'))
            current = current.GetProperty(segment);

        current.GetString().Should().Be(expected);
    }

    private sealed class RecordingLLMProvider : ILLMProvider, ILLMProviderFactory
    {
        public string Name => "recording";

        public LLMRequest? LastRequest { get; private set; }

        public int StreamCallCount { get; private set; }

        public IReadOnlyList<LLMStreamChunk> StreamChunks { get; init; } = [];

        public IReadOnlyList<IReadOnlyList<LLMStreamChunk>> StreamChunkBatches { get; init; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public Task<IReadOnlyList<LLMStreamChunk>> CollectForCommandAsync(
            LlmRunRequested command,
            CancellationToken ct = default)
        {
            CaptureDispatchedCommand(command);
            StreamCallCount++;
            var chunks = StreamCallCount <= StreamChunkBatches.Count
                ? StreamChunkBatches[StreamCallCount - 1]
                : StreamChunks;
            return Task.FromResult<IReadOnlyList<LLMStreamChunk>>(chunks);
        }

        public void CaptureDispatchedCommand(LlmRunRequested command)
        {
            LastRequest = ToLlmRequest(command);
        }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
            StreamCallCount++;
            var chunks = StreamCallCount <= StreamChunkBatches.Count
                ? StreamChunkBatches[StreamCallCount - 1]
                : StreamChunks;
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return chunk;
                await Task.Yield();
            }
        }

        private static LLMRequest ToLlmRequest(LlmRunRequested command)
        {
            var toolContext = command.ToolContext == null
                ? AgentToolExecutionContext.Empty
                : AgentToolExecutionContextMapper.FromPayload(command.ToolContext);
            return new LLMRequest
            {
                Messages = command.Messages.Select(ToChatMessage).ToList(),
                RequestId = command.ResponseId,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal),
                CallerContext = new LLMRequestCallerContext(
                    command.ScopeId,
                    command.OwnerSubject,
                    command.ResponseId,
                    new LLMRequestCallerCredentials(command.BearerToken)),
                Tools = ToEffectiveTools(command.ToolSelection),
                ToolContext = toolContext,
                LlmControl = new LLMControlContext(
                    NyxIdAccessToken: null,
                    NyxIdOrgToken: null,
                    SenderNyxIdAccessToken: null,
                    ModelOverride: null,
                    NyxIdRoutePreference: string.IsNullOrWhiteSpace(command.RoutePreference) ? null : command.RoutePreference,
                    MaxToolRoundsOverride: null,
                    UserMemoryPrompt: null),
                Model = command.Model,
                Temperature = command.HasTemperature ? command.Temperature : null,
                MaxTokens = command.HasMaxTokens ? command.MaxTokens : null,
            };
        }

        private static ChatMessage ToChatMessage(LlmSessionRuntimeChatMessage message)
        {
            var toolCalls = message.ToolCalls.Count == 0
                ? null
                : message.ToolCalls
                    .Select(static call => new ToolCall
                    {
                        Id = call.CallId,
                        Name = call.ToolName,
                        ArgumentsJson = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson,
                    })
                    .ToList();
            var result = new ChatMessage
            {
                Role = message.Role,
                Content = message.Content,
                ReasoningContent = string.IsNullOrWhiteSpace(message.ReasoningContent) ? null : message.ReasoningContent,
                ToolCallId = string.IsNullOrWhiteSpace(message.ToolCallId) ? null : message.ToolCallId,
                ToolCalls = toolCalls,
            };
            return result;
        }

        private static IReadOnlyList<IAgentTool> ToEffectiveTools(LlmSessionRuntimeToolSelection? selection)
        {
            if (selection == null)
                return [];

            var tools = new List<IAgentTool>();
            var ownedToolNames = selection.OwnedToolNames.Count > 0
                ? selection.OwnedToolNames.ToHashSet(StringComparer.Ordinal)
                : selection.SubstitutedToolNames.Concat(selection.AdditiveToolNames).ToHashSet(StringComparer.Ordinal);
            tools.AddRange(selection.ForwardedTools
                .Where(tool => !ownedToolNames.Contains(tool.ToolName))
                .Select(static tool =>
                    new StubAgentTool(tool.ToolName, tool.Description, tool.ParametersJson)));
            tools.AddRange(selection.SubstitutedToolNames.Select(static name =>
                new StubAgentTool(name, $"{name} substitute")));
            tools.AddRange(selection.AdditiveToolNames.Select(static name =>
                new StubAgentTool(name, $"{name} additive")));
            return tools
                .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
                .Select(static group => group.First())
                .ToArray();
        }
    }

    private sealed class RecordingLLMModelDiscoveryApplicationService
        : ILLMModelDiscoveryApplicationService
    {
        public string? LastBearer { get; private set; }
        public string? LastScopeId { get; private set; }
        public int CallCount { get; private set; }
        public IReadOnlyList<LLMModelDescriptor> Entries { get; init; } = [];
        public Exception? Exception { get; init; }

        public Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(
            string scopeId,
            CancellationToken ct = default)
        {
            LastScopeId = scopeId;
            CallCount++;
            return Exception is null
                ? Task.FromResult(Entries)
                : Task.FromException<IReadOnlyList<LLMModelDescriptor>>(Exception);
        }
    }

    private sealed class RecordingResponsesRouteResolver : IResponsesRouteResolver
    {
        public Dictionary<string, LLMRouteTarget> Routes { get; init; } = new(StringComparer.Ordinal);
        public int CallCount { get; private set; }
        public string? LastSlug { get; private set; }

        public Task<LLMRouteTarget?> ResolveRouteTargetAsync(
            string serviceSlug,
            string upstreamModelId,
            ResponsesCallerScope callerScope,
            CancellationToken ct)
        {
            CallCount++;
            LastSlug = serviceSlug;
            return Task.FromResult(
                Routes.TryGetValue(serviceSlug, out var target) ? target.Clone() : null);
        }
    }

    private sealed class StaticChatRoutePolicyQueryPort(ChatRoutePolicySnapshot? snapshot) : IChatRoutePolicyQueryPort
    {
        public static StaticChatRoutePolicyQueryPort ForSnapshot(ChatRoutePolicySnapshot? snapshot) => new(snapshot);

        public Task<ChatRoutePolicySnapshot?> LookupForCallerAsync(
            OwnerScope callerScope,
            CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class StaticChatRouteFallbackProvider(string modelName) : IChatRouteFallbackProvider
    {
        public ChatRouteDecision GetFallbackDecision() => new()
        {
            Action = ForwardToModelAction(modelName),
            UsedFallback = true,
            MatchedRuleId = string.Empty,
            ResolvedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
    }

    private static ChatRouteAction ForwardToModelAction(string modelName) => new()
    {
        ForwardToModel = new ForwardToModel { ModelName = modelName },
    };

    private static ChatRouteAction RejectAction(string code, string message) => new()
    {
        Reject = new Reject { Reason = message },
    };

    private static ChatRouteAction GAgentToolHintAction(string actorId) =>
        ToolHintAction(
            "aevatar_invoke_gagent",
            [new("actor_id", actorId)]);

    private static ChatRouteAction TeamToolHintAction(string teamId, string endpointId) =>
        ToolHintAction(
            "aevatar_invoke_team",
            [
                new("team_id", teamId),
                new("endpoint_id", endpointId),
            ]);

    private static IResponsesOwnedToolCatalogPlanner GAgentOwnedToolCatalogPlanner() =>
        new FixedResponsesOwnedToolCatalogPlanner(
            ToolSetNames.WorkspaceDefault,
            new StubAgentTool("aevatar_invoke_gagent", "Invoke a GAgent"));

    private static IResponsesOwnedToolCatalogPlanner TeamOwnedToolCatalogPlanner() =>
        new FixedResponsesOwnedToolCatalogPlanner(
            ToolSetNames.WorkspaceDefault,
            new StubAgentTool("aevatar_invoke_team", "Invoke a team"));

    private static ChatRouteAction ToolHintAction(
        string toolName,
        IReadOnlyList<(string Name, string Value)> arguments)
    {
        var prefilledArguments = new Struct();
        foreach (var (name, value) in arguments)
            prefilledArguments.Fields[name] = Google.Protobuf.WellKnownTypes.Value.ForString(value);

        return new ChatRouteAction
        {
            ForwardToModel = new ForwardToModel
            {
                ToolSetRef = new ChatRouteToolSetRef { Name = ToolSetNames.WorkspaceDefault },
                ToolChoiceHint = new ChatRouteToolChoiceHint
                {
                    ToolName = toolName,
                    PrefilledArguments = prefilledArguments,
                },
            },
        };
    }

    private sealed class StubResponsesCallerScopeResolver : IResponsesCallerScopeResolver
    {
        private readonly ResponsesCallerScope _scope;

        public StubResponsesCallerScopeResolver(
            string scopeId = "user-1",
            string ownerSubject = "user-1",
            LlmSessionOriginKind originKind = LlmSessionOriginKind.ApiKey)
        {
            _scope = new ResponsesCallerScope(scopeId, ownerSubject, originKind);
        }

        public ResponsesCallerScopeResolutionContext? LastContext { get; private set; }

        public Task<ResponsesCallerScope> ResolveAsync(
            ResponsesCallerScopeResolutionContext context,
            CancellationToken ct = default)
        {
            LastContext = context;
            return Task.FromResult(_scope);
        }
    }

    private sealed class TokenAwareResponsesCallerScopeResolver(ResponsesCallerScope scope)
        : IResponsesCallerScopeResolver
    {
        public List<string> ResolvedTokens { get; } = [];

        public Task<ResponsesCallerScope> ResolveAsync(
            ResponsesCallerScopeResolutionContext context,
            CancellationToken ct = default)
        {
            ResolvedTokens.Add(context.InboundBearerToken);
            return Task.FromResult(scope);
        }
    }

    private sealed class RecordingResponsesToolProvider : IResponsesToolProvider
    {
        private readonly IReadOnlyList<IAgentTool> _substituteTools;
        private readonly IReadOnlyList<IAgentTool> _additiveTools;

        public RecordingResponsesToolProvider(
            IReadOnlyList<IAgentTool> substituteTools,
            IReadOnlyList<IAgentTool> additiveTools)
        {
            _substituteTools = substituteTools;
            _additiveTools = additiveTools;
        }

        public ValueTask<IReadOnlyList<IAgentTool>> GetSubstituteToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default) =>
            ValueTask.FromResult(_substituteTools);

        public ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default) =>
            ValueTask.FromResult(_additiveTools);
    }

    private sealed class StubAgentToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class ThrowingAgentToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<IAgentTool>>(
                new InvalidOperationException("source discovery failed"));
    }

    private sealed class CanceledAgentToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<IAgentTool>>(new OperationCanceledException(ct));
    }

    private sealed class NotFoundHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static ResponsesToolProviderContext BuildToolProviderContext(
        ResponsesCallerScope callerScope,
        string responseId,
        string bearerToken)
    {
        return new ResponsesToolProviderContext(
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity(responseId, null),
                Credentials = new AgentToolCredentials(bearerToken, null, null),
                Caller = new AgentToolCallerContext(callerScope.ScopeId, callerScope.OwnerSubject, responseId),
                Channel = new AgentToolChannelContext(
                    callerScope.OriginKind.ToString(),
                    null,
                    callerScope.ScopeId,
                    null,
                    null),
            });
    }

    private static ResponsesAevatarToolProvider CreateResponsesAevatarToolProvider(
        RecordingResponsesAgentToolStatePort port)
    {
        var service = new ResponsesWebSubstituteToolExecutionService(
            port,
            port,
            new StubResponsesWebSubstituteBackend());
        return new ResponsesAevatarToolProvider(port, service);
    }

    private sealed class StubResponsesWebSubstituteBackend : IResponsesWebSubstituteBackend
    {
        public int DefaultMaxSearchResults => 10;

        public Task<ResponsesWebFetchBoundaryResult> ExecuteWebFetchAsync(
            ResponsesWebFetchBoundaryInput input,
            CancellationToken ct) =>
            Task.FromResult(new ResponsesWebFetchBoundaryResult(
                input.Url,
                200,
                "text/plain",
                "body",
                string.Empty));

        public Task<ResponsesWebSearchBoundaryResult> ExecuteWebSearchAsync(
            ResponsesWebSearchBoundaryInput input,
            CancellationToken ct) =>
            Task.FromResult(new ResponsesWebSearchBoundaryResult(
                ResponsesWebResultMigration.FromSearch(new ResponsesWebSearchToolOutput())));
    }

    private sealed class StubAgentTool : IAgentTool
    {
        public StubAgentTool(
            string name,
            string description,
            string parametersSchema = """{"type":"object","properties":{}}""")
        {
            Name = name;
            Description = description;
            ParametersSchema = parametersSchema;
        }

        public string Name { get; }

        public string Description { get; }

        public string ParametersSchema { get; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("""{"ok":true}""");
    }

    private sealed class RecordingResponsesAgentToolStatePort :
        IResponsesAgentToolStateCommandPort,
        IResponsesAgentToolStateQueryPort
    {
        private readonly Dictionary<(string ToolName, string CacheKey), ResponsesWebCacheEntrySnapshot> _webCache =
            new();

        public List<(string ScopeId, string OwnerSubject, string SourceResponseId, string ArgumentsJson)> TodoWrites { get; } = [];

        public List<(string ScopeId, string OwnerSubject, string SourceResponseId, ResponsesWebTraceInput Trace)> WebTraces { get; } = [];

        public string SeedWebCache(string toolName, string value, string resultJson)
        {
            var cacheKey = ComputeCacheKey(toolName, value);
            _webCache[(toolName, cacheKey)] = new ResponsesWebCacheEntrySnapshot(
                cacheKey,
                toolName,
                value,
                string.Empty,
                ResponsesWebResultMigration.FromLegacyValue(ResponsesJsonValues.ParseBoundaryPayload(resultJson)),
                DateTimeOffset.UtcNow,
                null,
                0);
            return cacheKey;
        }

        public Task<ResponsesTodoWriteResult> ApplyTodoWriteAsync(
            string scopeId,
            string ownerSubject,
            string sourceResponseId,
            string argumentsJson,
            CancellationToken ct = default)
        {
            TodoWrites.Add((scopeId, ownerSubject, sourceResponseId, argumentsJson));
            return Task.FromResult(new ResponsesTodoWriteResult(
                "responses-agent-tools-test",
                sourceResponseId,
                [
                    new ResponsesTodoItemSnapshot(
                        "todo-1",
                        "Ship prototype",
                        "in_progress",
                        sourceResponseId,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow),
                ]));
        }

        public Task<ResponsesWebTraceResult> RecordWebTraceAsync(
            string scopeId,
            string ownerSubject,
            string sourceResponseId,
            ResponsesWebTraceInput trace,
            CancellationToken ct = default)
        {
            WebTraces.Add((scopeId, ownerSubject, sourceResponseId, trace));
            return Task.FromResult(new ResponsesWebTraceResult(
                "responses-agent-tools-test",
                trace.TraceId,
                trace.CacheKey,
                trace.CacheHit,
                trace.Result.Clone()));
        }

        public Task<ResponsesAgentToolStateSnapshot?> GetAsync(
            string scopeId,
            string ownerSubject,
            CancellationToken ct = default) =>
            Task.FromResult<ResponsesAgentToolStateSnapshot?>(null);

        public Task<ResponsesWebCacheEntrySnapshot?> GetWebCacheEntryAsync(
            string scopeId,
            string ownerSubject,
            string toolName,
            string cacheKey,
            CancellationToken ct = default)
        {
            _webCache.TryGetValue((toolName, cacheKey), out var entry);
            return Task.FromResult(entry);
        }

        private static string ComputeCacheKey(string toolName, string value)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes($"{toolName}\n{value.Trim().ToLowerInvariant()}"));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    private sealed class RecordingResponseSessionStore :
        ILlmSessionRegistrationPort,
        ILlmSessionQueryPort
    {
        private readonly Dictionary<string, LlmSessionSnapshot> _snapshots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _completionObservationLagReads = new(StringComparer.Ordinal);

        public List<LlmSessionRecord> Registered { get; } = [];

        public List<(string ActorId, string ResponseId, LlmSessionStatus Status)> StatusUpdates { get; } = [];

        public List<(string ActorId, string ResponseId, string RunId)> CancelledRuns { get; } = [];

        public List<(string ActorId, string ResponseId, LlmSessionForwardedToolCall Call)> ForwardedToolCalls { get; } = [];

        public List<(string ActorId, string ResponseId, LlmSessionCompletion Completion)> RecordedCompletions { get; } = [];

        public List<(string ActorId, string ResponseId, string CallId, string SchemaHash, string ResultJson)> ToolResults { get; } = [];

        public List<(string ActorId, string ResponseId, string CallId)> ResolvedToolResults { get; } = [];

        public int CompletionObservationLagReads { get; init; }

        public void Seed(LlmSessionSnapshot snapshot)
        {
            _snapshots[snapshot.ResponseId] = snapshot;
        }

        public Task<LlmSessionRegistrationResult> RegisterAsync(
            LlmSessionRecord record,
            CancellationToken ct = default)
        {
            var clone = record.Clone();
            Registered.Add(clone);
            var actorId = LlmSessionIds.NewActorId();
            _snapshots[clone.ResponseId] = new LlmSessionSnapshot(
                clone.ResponseId,
                clone.ScopeId,
                clone.OwnerSubject,
                clone.OriginKind,
                string.IsNullOrWhiteSpace(clone.PreviousResponseId) ? null : clone.PreviousResponseId,
                clone.Status,
                clone.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow,
                clone.Ttl?.ToTimeSpan() ?? TimeSpan.Zero,
                clone.CancelledAt?.ToDateTimeOffset(),
                actorId,
                1,
                $"{clone.ResponseId}:registered");
            return Task.FromResult(new LlmSessionRegistrationResult(actorId, clone.ResponseId));
        }

        public Task UpdateStatusAsync(
            string sessionActorId,
            string responseId,
            LlmSessionStatus status,
            CancellationToken ct = default)
        {
            StatusUpdates.Add((sessionActorId, responseId, status));
            if (_snapshots.TryGetValue(responseId, out var current))
            {
                _snapshots[responseId] = current with
                {
                    Status = status,
                    CancelledAt = status == LlmSessionStatus.Cancelled
                        ? DateTimeOffset.UtcNow
                        : current.CancelledAt,
                    ForwardedToolCalls = MarkCallsForStatus(current.ForwardedToolCalls, status),
                    StateVersion = current.StateVersion + 1,
                    LastEventId = $"{responseId}:status:{(int)status}",
                };
            }
            return Task.CompletedTask;
        }

        public Task CancelRunAsync(
            string sessionActorId,
            string responseId,
            string runId,
            CancellationToken ct = default)
        {
            CancelledRuns.Add((sessionActorId, responseId, runId));
            if (_snapshots.TryGetValue(responseId, out var current))
            {
                _snapshots[responseId] = current with
                {
                    Status = LlmSessionStatus.Cancelled,
                    CancelledAt = DateTimeOffset.UtcNow,
                    ForwardedToolCalls = MarkCallsForStatus(current.ForwardedToolCalls, LlmSessionStatus.Cancelled),
                    StateVersion = current.StateVersion + 1,
                    LastEventId = $"{responseId}:run:{runId}:cancelled",
                    Completion = new LlmSessionCompletionSnapshot(
                        current.Completion?.OutputText ?? string.Empty,
                        current.Completion?.ToolCalls ?? [],
                        DateTimeOffset.UtcNow,
                        "request_cancelled",
                        "LLM run was cancelled.",
                        current.Completion?.Usage),
                };
            }

            return Task.CompletedTask;
        }

        public Task RecordForwardedToolCallAsync(
            string sessionActorId,
            string responseId,
            LlmSessionForwardedToolCall call,
            CancellationToken ct = default)
        {
            var clone = call.Clone();
            ForwardedToolCalls.Add((sessionActorId, responseId, clone));
            if (_snapshots.TryGetValue(responseId, out var current))
            {
                var calls = (current.ForwardedToolCalls ?? [])
                    .Where(existing => !string.Equals(existing.CallId, clone.CallId, StringComparison.Ordinal))
                    .Append(new LlmSessionForwardedToolCallSnapshot(
                        clone.CallId,
                        clone.ToolName,
                        clone.SchemaHash,
                        ResponsesJsonValues.ToBoundaryJson(clone.Arguments),
                        clone.Status,
                        clone.Expiry?.ToDateTimeOffset(),
                        string.IsNullOrWhiteSpace(ResponsesJsonValues.ToBoundaryJson(clone.Result))
                            ? null
                            : ResponsesJsonValues.ToBoundaryJson(clone.Result),
                        clone.EmittedAt?.ToDateTimeOffset(),
                        clone.ReceivedAt?.ToDateTimeOffset(),
                        clone.ResolvedAt?.ToDateTimeOffset()))
                    .ToArray();
                _snapshots[responseId] = current with
                {
                    ForwardedToolCalls = calls,
                    StateVersion = current.StateVersion + 1,
                    LastEventId = $"{responseId}:tool:{clone.CallId}:emitted",
                };
            }

            return Task.CompletedTask;
        }

        public Task RecordCompletionAsync(
            string sessionActorId,
            string responseId,
            LlmSessionCompletion completion,
            CancellationToken ct = default)
        {
            var clone = completion.Clone();
            RecordedCompletions.Add((sessionActorId, responseId, clone));
            if (_snapshots.TryGetValue(responseId, out var current))
            {
                _snapshots[responseId] = current with
                {
                    Status = string.IsNullOrWhiteSpace(clone.FailureCode)
                        ? LlmSessionStatus.Completed
                        : LlmSessionStatus.Failed,
                    StateVersion = current.StateVersion + 1,
                    LastEventId = $"{responseId}:completion",
                    Completion = new LlmSessionCompletionSnapshot(
                        clone.OutputText ?? string.Empty,
                        clone.ToolCalls
                            .Select(static call => new LlmSessionCompletedToolCallSnapshot(
                                call.CallId,
                                call.ToolName,
                                ResponsesJsonValues.ToBoundaryJson(call.Result)))
                            .ToArray(),
                        clone.CompletedAt?.ToDateTimeOffset(),
                        string.IsNullOrWhiteSpace(clone.FailureCode) ? null : clone.FailureCode,
                        string.IsNullOrWhiteSpace(clone.FailureMessage) ? null : clone.FailureMessage,
                        clone.Usage is null
                            ? null
                            : new TokenUsage(
                                clone.Usage.PromptTokens,
                                clone.Usage.CompletionTokens,
                                clone.Usage.TotalTokens)),
                };
            }
            if (CompletionObservationLagReads > 0)
            {
                _completionObservationLagReads[responseId] = CompletionObservationLagReads;
            }

            return Task.CompletedTask;
        }

        public Task ReceiveForwardedToolResultAsync(
            string sessionActorId,
            string responseId,
            string callId,
            string schemaHash,
            string resultJson,
            CancellationToken ct = default)
        {
            ToolResults.Add((sessionActorId, responseId, callId, schemaHash, resultJson));
            if (_snapshots.TryGetValue(responseId, out var current))
            {
                var calls = (current.ForwardedToolCalls ?? [])
                    .Select(call => string.Equals(call.CallId, callId, StringComparison.Ordinal)
                        ? call with
                        {
                            Status = LlmSessionForwardedToolCallStatus.Received,
                            ResultJson = resultJson,
                            ReceivedAt = DateTimeOffset.UtcNow,
                        }
                        : call)
                    .ToArray();
                _snapshots[responseId] = current with
                {
                    ForwardedToolCalls = calls,
                    StateVersion = current.StateVersion + 1,
                    LastEventId = $"{responseId}:tool:{callId}:received",
                };
            }

            return Task.CompletedTask;
        }

        public Task ResolveForwardedToolResultAsync(
            string sessionActorId,
            string responseId,
            string callId,
            CancellationToken ct = default)
        {
            ResolvedToolResults.Add((sessionActorId, responseId, callId));
            if (_snapshots.TryGetValue(responseId, out var current))
            {
                var calls = (current.ForwardedToolCalls ?? [])
                    .Select(call => string.Equals(call.CallId, callId, StringComparison.Ordinal)
                        ? call with
                        {
                            Status = LlmSessionForwardedToolCallStatus.Resolved,
                            ResolvedAt = DateTimeOffset.UtcNow,
                        }
                        : call)
                    .ToArray();
                _snapshots[responseId] = current with
                {
                    ForwardedToolCalls = calls,
                    StateVersion = current.StateVersion + 1,
                    LastEventId = $"{responseId}:tool:{callId}:resolved",
                };
            }

            return Task.CompletedTask;
        }

        public Task<LlmSessionSnapshot?> GetByResponseIdAsync(
            string responseId,
            CancellationToken ct = default)
        {
            _snapshots.TryGetValue(responseId, out var snapshot);
            if (snapshot?.Completion is not null &&
                _completionObservationLagReads.TryGetValue(responseId, out var remaining) &&
                remaining > 0)
            {
                _completionObservationLagReads[responseId] = remaining - 1;
                return Task.FromResult<LlmSessionSnapshot?>(snapshot with { Completion = null });
            }

            return Task.FromResult(snapshot);
        }

        private static IReadOnlyList<LlmSessionForwardedToolCallSnapshot>? MarkCallsForStatus(
            IReadOnlyList<LlmSessionForwardedToolCallSnapshot>? calls,
            LlmSessionStatus status)
        {
            if (calls is not { Count: > 0 } ||
                status is not (LlmSessionStatus.Cancelled or LlmSessionStatus.Expired))
            {
                return calls;
            }

            return calls
                .Select(call =>
                {
                    if (call.Status is not (LlmSessionForwardedToolCallStatus.Pending
                        or LlmSessionForwardedToolCallStatus.Received))
                    {
                        return call;
                    }

                    var callStatus = status == LlmSessionStatus.Cancelled
                        ? LlmSessionForwardedToolCallStatus.Cancelled
                        : LlmSessionForwardedToolCallStatus.Expired;
                    return call with
                    {
                        Status = callStatus,
                        ResultJson = callStatus == LlmSessionForwardedToolCallStatus.Expired &&
                                     string.IsNullOrWhiteSpace(call.ResultJson)
                            ? $$"""{"error":"tool_call_expired","call_id":"{{call.CallId}}"}"""
                            : call.ResultJson,
                        ReceivedAt = callStatus == LlmSessionForwardedToolCallStatus.Expired
                            ? DateTimeOffset.UtcNow
                            : call.ReceivedAt,
                    };
                })
                .ToArray();
        }
    }
}
