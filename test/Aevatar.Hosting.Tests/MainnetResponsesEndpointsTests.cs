using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Authentication.Hosting;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.Mainnet.Host.Api.Responses;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Hosting.Tests;

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
        root.GetProperty("output")[0].GetProperty("type").GetString().Should().Be("message");
        root.GetProperty("output")[0].GetProperty("content")[0].GetProperty("type").GetString()
            .Should()
            .Be("output_text");
        root.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString()
            .Should()
            .Be("pong");
        root.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(3);
        root.GetProperty("usage").GetProperty("input_tokens_details").GetProperty("cached_tokens")
            .GetInt32()
            .Should()
            .Be(0);
        root.GetProperty("usage").GetProperty("output_tokens").GetInt32().Should().Be(2);
        root.GetProperty("usage").GetProperty("total_tokens").GetInt32().Should().Be(5);

        provider.ChatCallCount.Should().Be(0);
        provider.StreamCallCount.Should().Be(1);
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Model.Should().Be("gpt-5.4");
        provider.LastRequest.MaxTokens.Should().Be(128);
        provider.LastRequest.Temperature.Should().Be(0.2);
        provider.LastRequest.Messages.Should().ContainSingle();
        provider.LastRequest.Messages[0].Content.Should().Be("ping");
        provider.LastRequest.Metadata.Should().ContainKey(LLMRequestMetadataKeys.RequestId);
        provider.LastRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.ScopeId);
        provider.LastRequest.CallerContext.Should().Be(new LLMRequestCallerContext(
            "user-1",
            "user-1",
            responseId,
            new LLMRequestCallerCredentials("secret-token")));
        // The NyxID bearer is carried on the typed CallerContext.Credentials channel,
        // NOT through LLMRequest.Metadata. Metadata is the log-shaped string-keyed bag
        // that telemetry sinks may serialize; secret material belongs out-of-band.
        // Tool providers read the bearer from AgentToolRequestContext (separate path).
        provider.LastRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        provider.LastRequest.CallerContext!.Credentials!.NyxIdBearer.Should().Be("secret-token");

        sessions.Registered.Should().ContainSingle();
        sessions.Registered[0].ScopeId.Should().Be("user-1");
        sessions.Registered[0].OwnerSubject.Should().Be("user-1");
        sessions.Registered[0].OriginKind.Should().Be(ResponseSessionOriginKind.ApiKey);
        var snapshot = await sessions.GetByResponseIdAsync(responseId);
        snapshot!.ActorId.Should().NotContain(responseId);
        sessions.StatusUpdates.Should().ContainSingle(x => x.Status == ResponseSessionStatus.Completed);
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
        sessions.StatusUpdates.Should().ContainSingle(x => x.Status == ResponseSessionStatus.Completed);
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
        var output = doc.RootElement.GetProperty("output");
        output.GetArrayLength().Should().Be(2);
        output[1].GetProperty("type").GetString().Should().Be("function_call");
        output[1].GetProperty("call_id").GetString().Should().Be("call_weather_1");
        output[1].GetProperty("name").GetString().Should().Be("get_weather");
        output[1].GetProperty("arguments").GetString().Should().Be("""{"city":"Singapore"}""");

        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Tools.Should().ContainSingle();
        sessions.ForwardedToolCalls.Should().ContainSingle();
        var persisted = sessions.ForwardedToolCalls[0].Call;
        persisted.CallId.Should().Be("call_weather_1");
        persisted.ToolName.Should().Be("get_weather");
        persisted.SchemaHash.Should().Be(ResponsesToolSchemaHashes.Compute(parametersJson));
        ResponsesJsonValues.ToBoundaryJson(persisted.Arguments).Should().Be("""{"city":"Singapore"}""");
        persisted.Status.Should().Be(ResponseSessionForwardedToolCallStatus.Pending);
        persisted.Expiry.Should().NotBeNull();
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
        await using var app = await CreateAppAsync(provider, sessions, responsesToolProvider: toolProvider);
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
        provider.StreamCallCount.Should().Be(2);
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Tools.Should().HaveCount(2);
        provider.LastRequest.Tools![0].Name.Should().Be("Task");
        provider.LastRequest.Tools[0].Description.Should().Be("Aevatar task dispatcher");
        provider.LastRequest.Tools[0].ParametersSchema.Should().Be("""{"type":"object","properties":{}}""");
        provider.LastRequest.Tools[1].Name.Should().Be("aevatar_notes");
        provider.LastRequest.Messages.Should().HaveCount(3);
        provider.LastRequest.Messages[1].ToolCalls.Should().ContainSingle()
            .Which.Name.Should().Be("Task");
        provider.LastRequest.Messages[2].ToolCallId.Should().Be("call_task_1");
        sessions.ForwardedToolCalls.Should().BeEmpty();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString()
            .Should()
            .Be("delegated");
        doc.RootElement.GetProperty("output").EnumerateArray()
            .Should()
            .NotContain(item => item.GetProperty("type").GetString() == "function_call");
    }

    [Fact]
    public async Task AevatarSubstituteTools_ShouldPersistTodoAndTaskThroughAgentToolStatePort()
    {
        var commandPort = new RecordingResponsesAgentToolStatePort();
        var provider = new ResponsesAevatarToolProvider(
            commandPort,
            commandPort,
            new Aevatar.AI.ToolProviders.Web.WebApiClient(new Aevatar.AI.ToolProviders.Web.WebToolOptions()),
            new Aevatar.AI.ToolProviders.Web.WebToolOptions());

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.ScopeId] = "scope-1",
            [LLMRequestMetadataKeys.OwnerSubject] = "owner-1",
            [LLMRequestMetadataKeys.ResponseId] = "resp_1",
        };
        var previous = AgentToolRequestContext.CurrentMetadata;
        try
        {
            AgentToolRequestContext.CurrentMetadata = metadata;
            var todoTool = provider.GetSubstituteTools().Single(x => x.Name == "TodoWrite");
            var todoResult = await todoTool.ExecuteAsync(
                """{"todos":[{"id":"todo-1","content":"Ship prototype","status":"in_progress"}]}""");

            var taskTool = provider.GetSubstituteTools().Single(x => x.Name == "Task");
            var taskResult = await taskTool.ExecuteAsync("""{"prompt":"summarize state"}""");

            todoResult.Should().Contain("stored");
            taskResult.Should().Contain("accepted");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = previous;
        }

        commandPort.TodoWrites.Should().ContainSingle();
        commandPort.TodoWrites[0].ScopeId.Should().Be("scope-1");
        commandPort.TodoWrites[0].OwnerSubject.Should().Be("owner-1");
        commandPort.TodoWrites[0].SourceResponseId.Should().Be("resp_1");
        commandPort.Tasks.Should().ContainSingle();
        commandPort.Tasks[0].ArgumentsJson.Should().Contain("summarize state");
    }

    [Fact]
    public async Task AevatarWebFetchSubstitute_ShouldUseCachedReadModelAndRecordTrace()
    {
        var commandPort = new RecordingResponsesAgentToolStatePort();
        var cacheKey = commandPort.SeedWebCache(
            "WebFetch",
            "https://example.com/docs",
            """{"url":"https://example.com/docs","content":"cached"}""");
        var provider = new ResponsesAevatarToolProvider(
            commandPort,
            commandPort,
            new Aevatar.AI.ToolProviders.Web.WebApiClient(new Aevatar.AI.ToolProviders.Web.WebToolOptions()),
            new Aevatar.AI.ToolProviders.Web.WebToolOptions());

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.ScopeId] = "scope-1",
            [LLMRequestMetadataKeys.OwnerSubject] = "owner-1",
            [LLMRequestMetadataKeys.ResponseId] = "resp_1",
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token",
        };
        var previous = AgentToolRequestContext.CurrentMetadata;
        try
        {
            AgentToolRequestContext.CurrentMetadata = metadata;
            var fetchTool = provider.GetSubstituteTools().Single(x => x.Name == "WebFetch");
            var result = await fetchTool.ExecuteAsync("""{"url":"https://example.com/docs"}""");

            result.Should().Contain("cached");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = previous;
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
        var provider = new ResponsesAevatarToolProvider(
            commandPort,
            commandPort,
            new Aevatar.AI.ToolProviders.Web.WebApiClient(new Aevatar.AI.ToolProviders.Web.WebToolOptions()),
            new Aevatar.AI.ToolProviders.Web.WebToolOptions());

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.ScopeId] = "scope-1",
            [LLMRequestMetadataKeys.OwnerSubject] = "owner-1",
            [LLMRequestMetadataKeys.ResponseId] = "resp_1",
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "token",
        };
        var previous = AgentToolRequestContext.CurrentMetadata;
        try
        {
            AgentToolRequestContext.CurrentMetadata = metadata;
            var searchTool = provider.GetSubstituteTools().Single(x => x.Name == "WebSearch");
            var result = await searchTool.ExecuteAsync("""{"query":"aevatar docs","max_results":3}""");

            result.Should().Contain("cached docs");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = previous;
        }

        commandPort.WebTraces.Should().ContainSingle();
        commandPort.WebTraces[0].Trace.CacheKey.Should().Be(cacheKey);
        commandPort.WebTraces[0].Trace.Query.Should().Be("aevatar docs");
        commandPort.WebTraces[0].Trace.CacheHit.Should().BeTrue();
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
        sessions.Seed(new ResponseSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            ResponseSessionOriginKind.ApiKey,
            null,
            ResponseSessionStatus.Completed,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_previous",
            2,
            "resp_previous:tool:call_1:emitted",
            [
                new ResponseSessionForwardedToolCallSnapshot(
                    "call_1",
                    "get_weather",
                    schemaHash,
                    """{"city":"Singapore"}""",
                    ResponseSessionForwardedToolCallStatus.Pending,
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
        sessions.Seed(new ResponseSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            ResponseSessionOriginKind.ApiKey,
            null,
            ResponseSessionStatus.Completed,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_previous",
            3,
            "resp_previous:tool:call_2:emitted",
            [
                new ResponseSessionForwardedToolCallSnapshot(
                    "call_1",
                    "get_weather",
                    schemaHash,
                    """{"city":"Singapore"}""",
                    ResponseSessionForwardedToolCallStatus.Pending,
                    DateTimeOffset.UtcNow.AddHours(1),
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    null,
                    null),
                new ResponseSessionForwardedToolCallSnapshot(
                    "call_2",
                    "get_time",
                    schemaHash,
                    """{"city":"Singapore"}""",
                    ResponseSessionForwardedToolCallStatus.Pending,
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
            .Should().Be(ResponseSessionForwardedToolCallStatus.Pending);
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
        sessions.Seed(new ResponseSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            ResponseSessionOriginKind.ApiKey,
            null,
            ResponseSessionStatus.Completed,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_previous",
            4,
            "resp_previous:tool:call_1:resolved",
            [
                new ResponseSessionForwardedToolCallSnapshot(
                    "call_1",
                    "get_weather",
                    schemaHash,
                    """{"city":"Singapore"}""",
                    ResponseSessionForwardedToolCallStatus.Resolved,
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
        sessions.Registered.Should().BeEmpty();
        sessions.ToolResults.Should().BeEmpty();
        sessions.ResolvedToolResults.Should().BeEmpty();
    }

    [Fact]
    public async Task PostResponses_WithFunctionCallOutputSchemaMismatch_ShouldReturnBadRequest()
    {
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();
        sessions.Seed(new ResponseSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            ResponseSessionOriginKind.ApiKey,
            null,
            ResponseSessionStatus.Completed,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_previous",
            2,
            "resp_previous:tool:call_1:emitted",
            [
                new ResponseSessionForwardedToolCallSnapshot(
                    "call_1",
                    "get_weather",
                    "expected-hash",
                    "{}",
                    ResponseSessionForwardedToolCallStatus.Pending,
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
        sessions.Seed(new ResponseSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            ResponseSessionOriginKind.ApiKey,
            null,
            ResponseSessionStatus.Completed,
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
    }

    [Fact]
    public async Task PostResponses_WithExpiredPreviousResponse_ShouldRejectResume()
    {
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();
        sessions.Seed(new ResponseSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            ResponseSessionOriginKind.ApiKey,
            null,
            ResponseSessionStatus.Completed,
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
        body.Should().Contain("previous_response_expired");
        sessions.Registered.Should().BeEmpty();
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponses_WithPreviousResponseFromDifferentScope_ShouldReturnForbidden()
    {
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();
        sessions.Seed(new ResponseSessionSnapshot(
            "resp_foreign",
            "other-user",
            "other-user",
            ResponseSessionOriginKind.ApiKey,
            null,
            ResponseSessionStatus.Completed,
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
        body.Should().Contain("response_scope_mismatch");
        sessions.Registered.Should().BeEmpty();
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponses_WithPreviousResponseFromDifferentOrigin_ShouldReturnForbidden()
    {
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();
        sessions.Seed(new ResponseSessionSnapshot(
            "resp_channel",
            "user-1",
            "user-1",
            ResponseSessionOriginKind.Channel,
            null,
            ResponseSessionStatus.Completed,
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
        body.Should().Contain("response_origin_mismatch");
        sessions.Registered.Should().BeEmpty();
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostResponsesCancel_ShouldMarkResponseAndPendingToolCallsCancelled()
    {
        var provider = new RecordingLLMProvider();
        var sessions = new RecordingResponseSessionStore();
        sessions.Seed(new ResponseSessionSnapshot(
            "resp_previous",
            "user-1",
            "user-1",
            ResponseSessionOriginKind.ApiKey,
            null,
            ResponseSessionStatus.Completed,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            TimeSpan.FromHours(1),
            null,
            "response-session:resp_previous",
            2,
            "resp_previous:tool:call_1:emitted",
            [
                new ResponseSessionForwardedToolCallSnapshot(
                    "call_1",
                    "get_weather",
                    "schema-1",
                    "{}",
                    ResponseSessionForwardedToolCallStatus.Pending,
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

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("id").GetString().Should().Be("resp_previous");
        doc.RootElement.GetProperty("status").GetString().Should().Be("cancelled");
        sessions.StatusUpdates.Should().ContainSingle(x => x.Status == ResponseSessionStatus.Cancelled);
        var snapshot = await sessions.GetByResponseIdAsync("resp_previous");
        snapshot!.Status.Should().Be(ResponseSessionStatus.Cancelled);
        snapshot.ForwardedToolCalls.Should().ContainSingle()
            .Which.Status.Should().Be(ResponseSessionForwardedToolCallStatus.Cancelled);
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
        builder.AddAevatarAuthentication();

        builder.Services.AddSingleton<ILLMProviderFactory>(provider);
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton<IResponseSessionRegistrationPort>(sessions);
        builder.Services.AddSingleton<IResponseSessionQueryPort>(sessions);
        builder.Services.AddSingleton<IResponsesCompletionApplicationService, ResponsesCompletionApplicationService>();
        builder.Services.AddSingleton<IResponsesCallerScopeResolver>(new StubResponsesCallerScopeResolver());

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

    private static async Task<WebApplication> CreateAppAsync(
        RecordingLLMProvider provider,
        RecordingResponseSessionStore? responseSessions = null,
        IResponsesCallerScopeResolver? callerScopeResolver = null,
        IResponsesToolProvider? responsesToolProvider = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ILLMProviderFactory>(provider);
        responseSessions ??= new RecordingResponseSessionStore();
        builder.Services.AddSingleton(responseSessions);
        builder.Services.AddSingleton<IResponseSessionRegistrationPort>(responseSessions);
        builder.Services.AddSingleton<IResponseSessionQueryPort>(responseSessions);
        builder.Services.AddSingleton<IResponsesCompletionApplicationService, ResponsesCompletionApplicationService>();
        builder.Services.AddSingleton(callerScopeResolver ?? new StubResponsesCallerScopeResolver());
        if (responsesToolProvider != null)
            builder.Services.AddSingleton(responsesToolProvider);

        var app = builder.Build();
        app.MapResponsesApiEndpoints();
        await app.StartAsync();
        return app;
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private sealed class RecordingLLMProvider : ILLMProvider, ILLMProviderFactory
    {
        public string Name => "recording";

        public LLMRequest? LastRequest { get; private set; }

        public int ChatCallCount { get; private set; }

        public int StreamCallCount { get; private set; }

        public LLMResponse ChatResponse { get; init; } = new() { Content = "ok" };

        public IReadOnlyList<LLMStreamChunk> StreamChunks { get; init; } = [];

        public IReadOnlyList<IReadOnlyList<LLMStreamChunk>> StreamChunkBatches { get; init; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            ChatCallCount++;
            return Task.FromResult(ChatResponse);
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
    }

    private sealed class StubResponsesCallerScopeResolver : IResponsesCallerScopeResolver
    {
        private readonly ResponsesCallerScope _scope;

        public StubResponsesCallerScopeResolver(
            string scopeId = "user-1",
            string ownerSubject = "user-1",
            ResponseSessionOriginKind originKind = ResponseSessionOriginKind.ApiKey)
        {
            _scope = new ResponsesCallerScope(scopeId, ownerSubject, originKind);
        }

        public Task<ResponsesCallerScope> ResolveAsync(
            string nyxIdAccessToken,
            HttpContext http,
            CancellationToken ct = default) =>
            Task.FromResult(_scope);
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

        public IReadOnlyList<IAgentTool> GetSubstituteTools() => _substituteTools;

        public IReadOnlyList<IAgentTool> GetAdditiveTools() => _additiveTools;
    }

    private sealed class StubAgentTool : IAgentTool
    {
        public StubAgentTool(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public string Name { get; }

        public string Description { get; }

        public string ParametersSchema { get; } = """{"type":"object","properties":{}}""";

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

        public List<(string ScopeId, string OwnerSubject, string SourceResponseId, string ArgumentsJson)> Tasks { get; } = [];

        public List<(string ScopeId, string OwnerSubject, string SourceResponseId, ResponsesWebTraceInput Trace)> WebTraces { get; } = [];

        public string SeedWebCache(string toolName, string value, string resultJson)
        {
            var cacheKey = ComputeCacheKey(toolName, value);
            _webCache[(toolName, cacheKey)] = new ResponsesWebCacheEntrySnapshot(
                cacheKey,
                toolName,
                value,
                string.Empty,
                resultJson,
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

        public Task<ResponsesTaskDispatchResult> RecordTaskAsync(
            string scopeId,
            string ownerSubject,
            string sourceResponseId,
            string argumentsJson,
            CancellationToken ct = default)
        {
            Tasks.Add((scopeId, ownerSubject, sourceResponseId, argumentsJson));
            return Task.FromResult(new ResponsesTaskDispatchResult(
                "responses-agent-tools-test",
                "task_1",
                "responses-agent-tools-test-task-1",
                "accepted",
                """{"status":"accepted"}"""));
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
                trace.ResultJson));
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
        IResponseSessionRegistrationPort,
        IResponseSessionQueryPort
    {
        private readonly Dictionary<string, ResponseSessionSnapshot> _snapshots = new(StringComparer.Ordinal);

        public List<ResponseSessionRecord> Registered { get; } = [];

        public List<(string ActorId, string ResponseId, ResponseSessionStatus Status)> StatusUpdates { get; } = [];

        public List<(string ActorId, string ResponseId, ResponseSessionForwardedToolCall Call)> ForwardedToolCalls { get; } = [];

        public List<(string ActorId, string ResponseId, string CallId, string SchemaHash, string ResultJson)> ToolResults { get; } = [];

        public List<(string ActorId, string ResponseId, string CallId)> ResolvedToolResults { get; } = [];

        public void Seed(ResponseSessionSnapshot snapshot)
        {
            _snapshots[snapshot.ResponseId] = snapshot;
        }

        public Task<ResponseSessionRegistrationResult> RegisterAsync(
            ResponseSessionRecord record,
            CancellationToken ct = default)
        {
            var clone = record.Clone();
            Registered.Add(clone);
            var actorId = ResponseSessionIds.NewActorId();
            _snapshots[clone.ResponseId] = new ResponseSessionSnapshot(
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
            return Task.FromResult(new ResponseSessionRegistrationResult(actorId, clone.ResponseId));
        }

        public Task UpdateStatusAsync(
            string sessionActorId,
            string responseId,
            ResponseSessionStatus status,
            CancellationToken ct = default)
        {
            StatusUpdates.Add((sessionActorId, responseId, status));
            if (_snapshots.TryGetValue(responseId, out var current))
            {
                _snapshots[responseId] = current with
                {
                    Status = status,
                    CancelledAt = status == ResponseSessionStatus.Cancelled
                        ? DateTimeOffset.UtcNow
                        : current.CancelledAt,
                    ForwardedToolCalls = MarkCallsForStatus(current.ForwardedToolCalls, status),
                    StateVersion = current.StateVersion + 1,
                    LastEventId = $"{responseId}:status:{(int)status}",
                };
            }
            return Task.CompletedTask;
        }

        public Task RecordForwardedToolCallAsync(
            string sessionActorId,
            string responseId,
            ResponseSessionForwardedToolCall call,
            CancellationToken ct = default)
        {
            var clone = call.Clone();
            ForwardedToolCalls.Add((sessionActorId, responseId, clone));
            if (_snapshots.TryGetValue(responseId, out var current))
            {
                var calls = (current.ForwardedToolCalls ?? [])
                    .Where(existing => !string.Equals(existing.CallId, clone.CallId, StringComparison.Ordinal))
                    .Append(new ResponseSessionForwardedToolCallSnapshot(
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
                            Status = ResponseSessionForwardedToolCallStatus.Received,
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
                            Status = ResponseSessionForwardedToolCallStatus.Resolved,
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

        public Task<ResponseSessionSnapshot?> GetByResponseIdAsync(
            string responseId,
            CancellationToken ct = default)
        {
            _snapshots.TryGetValue(responseId, out var snapshot);
            return Task.FromResult(snapshot);
        }

        private static IReadOnlyList<ResponseSessionForwardedToolCallSnapshot>? MarkCallsForStatus(
            IReadOnlyList<ResponseSessionForwardedToolCallSnapshot>? calls,
            ResponseSessionStatus status)
        {
            if (calls is not { Count: > 0 } ||
                status is not (ResponseSessionStatus.Cancelled or ResponseSessionStatus.Expired))
            {
                return calls;
            }

            return calls
                .Select(call =>
                {
                    if (call.Status is not (ResponseSessionForwardedToolCallStatus.Pending
                        or ResponseSessionForwardedToolCallStatus.Received))
                    {
                        return call;
                    }

                    var callStatus = status == ResponseSessionStatus.Cancelled
                        ? ResponseSessionForwardedToolCallStatus.Cancelled
                        : ResponseSessionForwardedToolCallStatus.Expired;
                    return call with
                    {
                        Status = callStatus,
                        ResultJson = callStatus == ResponseSessionForwardedToolCallStatus.Expired &&
                                     string.IsNullOrWhiteSpace(call.ResultJson)
                            ? $$"""{"error":"tool_call_expired","call_id":"{{call.CallId}}"}"""
                            : call.ResultJson,
                        ReceivedAt = callStatus == ResponseSessionForwardedToolCallStatus.Expired
                            ? DateTimeOffset.UtcNow
                            : call.ReceivedAt,
                    };
                })
                .ToArray();
        }
    }
}
