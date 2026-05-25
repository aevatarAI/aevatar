using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.Authentication.Hosting;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.Mainnet.Host.Api.ChatCompletions;
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

public sealed class MainnetChatCompletionsEndpointsTests
{
    [Fact]
    public async Task PostChatCompletions_NonStreaming_ShouldReturnOpenAIEnvelope()
    {
        var provider = new ChatCompletionsRecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk
                {
                    DeltaContent = "Hi there",
                    IsLast = true,
                    Usage = new TokenUsage(5, 3, 8),
                },
            ],
        };
        var sessions = new ChatCompletionsRecordingSessionStore();
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent("""
            {
              "model": "gpt-4o-mini",
              "max_tokens": 256,
              "messages": [
                {"role": "system", "content": "You are concise."},
                {"role": "user", "content": "Hello"}
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "openai-bearer");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain("openai-bearer");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("id").GetString().Should().StartWith("chatcmpl_");
        root.GetProperty("object").GetString().Should().Be("chat.completion");
        root.GetProperty("model").GetString().Should().Be("gpt-4o-mini");
        var choice = root.GetProperty("choices")[0];
        choice.GetProperty("message").GetProperty("role").GetString().Should().Be("assistant");
        choice.GetProperty("message").GetProperty("content").GetString().Should().Be("Hi there");
        choice.GetProperty("finish_reason").GetString().Should().Be("stop");
        root.GetProperty("usage").GetProperty("prompt_tokens").GetInt32().Should().Be(5);
        root.GetProperty("usage").GetProperty("completion_tokens").GetInt32().Should().Be(3);

        sessions.Registered.Should().ContainSingle();
        sessions.Registered[0].ScopeId.Should().Be("user-1");
        sessions.StatusUpdates.Should().Contain(update => update.Status == LlmSessionStatus.Completed);

        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Messages.Should().HaveCount(2);
        provider.LastRequest.Messages[0].Role.Should().Be("system");
        provider.LastRequest.Messages[0].Content.Should().Be("You are concise.");
        provider.LastRequest.Messages[1].Role.Should().Be("user");
        provider.LastRequest.Messages[1].Content.Should().Be("Hello");
        provider.LastRequest.MaxTokens.Should().Be(256);
        provider.LastRequest.CallerContext!.Credentials!.NyxIdBearer.Should().Be("openai-bearer");
        provider.LastRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
    }

    [Fact]
    public async Task PostChatCompletions_Streaming_ShouldEmitOpenAIChunksAndDone()
    {
        var provider = new ChatCompletionsRecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "Hel" },
                new LLMStreamChunk
                {
                    DeltaContent = "lo",
                    IsLast = true,
                    Usage = new TokenUsage(4, 2, 6),
                },
            ],
        };
        var sessions = new ChatCompletionsRecordingSessionStore();
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent("""
            {
              "model": "gpt-4o-mini",
              "messages": [{"role": "user", "content": "ping"}],
              "stream": true,
              "stream_options": {"include_usage": true}
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "stream-bearer");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        body.Should().Contain("\"object\":\"chat.completion.chunk\"");
        body.Should().Contain("\"content\":\"Hel\"");
        body.Should().Contain("\"content\":\"lo\"");
        body.Should().Contain("\"finish_reason\":\"stop\"");
        body.Should().Contain("\"prompt_tokens\":4");
        body.Should().Contain("data: [DONE]");
        body.Should().NotContain("stream-bearer");
        sessions.StatusUpdates.Should().Contain(update => update.Status == LlmSessionStatus.Completed);
    }

    [Fact]
    public async Task PostChatCompletions_WithToolCall_ShouldReturnOpenAIToolCalls()
    {
        var provider = new ChatCompletionsRecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_abc",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":"SF"}""",
                    },
                    IsLast = true,
                },
            ],
        };
        await using var app = await CreateAppAsync(provider);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent("""
            {
              "model": "gpt-4o-mini",
              "messages": [{"role": "user", "content": "weather in SF"}],
              "tools": [
                {
                  "type": "function",
                  "function": {
                    "name": "get_weather",
                    "description": "Look up the weather.",
                    "parameters": {"type":"object","properties":{"city":{"type":"string"}}}
                  }
                }
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tool-bearer");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        var choice = doc.RootElement.GetProperty("choices")[0];
        choice.GetProperty("finish_reason").GetString().Should().Be("tool_calls");
        var toolCall = choice.GetProperty("message").GetProperty("tool_calls")[0];
        toolCall.GetProperty("id").GetString().Should().Be("call_abc");
        toolCall.GetProperty("type").GetString().Should().Be("function");
        toolCall.GetProperty("function").GetProperty("name").GetString().Should().Be("get_weather");
        toolCall.GetProperty("function").GetProperty("arguments").GetString().Should().Be("""{"city":"SF"}""");

        provider.LastRequest.Should().NotBeNull();
        var tool = provider.LastRequest!.Tools.Should().ContainSingle().Subject;
        tool.Name.Should().Be("get_weather");
        tool.ParametersSchema.Should().Contain("\"city\"");
    }

    [Fact]
    public async Task PostChatCompletions_WithModelSlug_ShouldResolveNyxRoutePreference()
    {
        var provider = new ChatCompletionsRecordingLLMProvider
        {
            StreamChunks = [new LLMStreamChunk { DeltaContent = "routed", IsLast = true }],
        };
        var routeResolver = new ChatCompletionsRecordingRouteResolver(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["chrono-llm"] = "/api/v1/proxy/s/chrono-llm",
        });
        await using var app = await CreateAppAsync(provider, routeResolver: routeResolver);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent("""
            {
              "model": "chrono-llm/gpt-5-chat",
              "messages": [{"role": "user", "content": "ping"}]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "route-bearer");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        routeResolver.ResolvedSlugs.Should().ContainSingle().Which.Should().Be("chrono-llm");
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Model.Should().Be("gpt-5-chat");
        provider.LastRequest.Metadata.Should().ContainKey(LLMRequestMetadataKeys.NyxIdRoutePreference)
            .WhoseValue.Should().Be("/api/v1/proxy/s/chrono-llm");
    }

    [Fact]
    public async Task PostChatCompletions_WhenRoutePinsTeamTool_ShouldExecuteThroughToolDrivenModelAction()
    {
        var provider = new ChatCompletionsRecordingLLMProvider
        {
            StreamChunks = [new LLMStreamChunk { DeltaContent = "tool-driven", IsLast = true }],
        };
        var queryPort = ChatCompletionsStaticChatRoutePolicyQueryPort.ForSnapshot(
            new ChatRoutePolicySnapshot(
                TeamToolHintAction("team-1", "chat"),
                []));
        await using var app = await CreateAppAsync(provider, chatRoutePolicyQueryPort: queryPort);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent("""
            {
              "model": "gpt-4o-mini",
              "messages": [{"role": "user", "content": "route to team"}]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "team-route-bearer");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Tools.Should().ContainSingle().Which.Name.Should().Be("aevatar_invoke_team");
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            .Should()
            .Be("tool-driven");
    }

    [Fact]
    public async Task PostChatCompletions_WithoutBearer_ShouldReturnOpenAIErrorEnvelope()
    {
        var provider = new ChatCompletionsRecordingLLMProvider();
        await using var app = await CreateAppAsync(provider);
        var client = app.GetTestClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            JsonContent("""{"model":"gpt-4o-mini","messages":[{"role":"user","content":"x"}]}"""));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("error");
        error.GetProperty("type").GetString().Should().Be("authentication_error");
        error.GetProperty("code").GetString().Should().Be("authentication_required");
    }

    [Fact]
    public async Task PostChatCompletions_WhenHostAuthEnabled_ShouldReachEndpointHandlerNotJwtBearerChallenge()
    {
        var provider = new ChatCompletionsRecordingLLMProvider();
        var sessions = new ChatCompletionsRecordingSessionStore();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration["Aevatar:Authentication:Authority"] = "https://invalid.example";
        builder.AddAevatarAuthentication();

        builder.Services.AddSingleton<ILLMProviderFactory>(provider);
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton<ILlmSessionRegistrationPort>(sessions);
        builder.Services.AddSingleton<IResponsesCompletionApplicationService, ResponsesCompletionApplicationService>();
        builder.Services.AddSingleton<IResponsesCallerScopeResolver>(new ChatCompletionsStubCallerScopeResolver());
        builder.Services.AddSingleton<IChatRoutePolicyQueryPort>(ChatCompletionsStaticChatRoutePolicyQueryPort.ForSnapshot(
            new ChatRoutePolicySnapshot(ForwardToModelAction(string.Empty), [])));
        builder.Services.AddSingleton(new ChatRouteResolver(new ChatCompletionsStaticChatRouteFallbackProvider(string.Empty)));
        builder.Services.AddSingleton<IResponsesRouteResolver>(new ChatCompletionsNoopRouteResolver());
        builder.Services.AddSingleton<IResponsesToolClassificationService, ResponsesToolClassificationService>();
        builder.Services.AddSingleton<IResponsesDirectToolPlanService, ResponsesDirectToolPlanService>();
        builder.Services.AddToolSetRegistry(options =>
        {
            options.AddToolSet(
                ToolSetNames.WorkspaceDefault,
                static _ => new StaticAgentToolSource([new ChatCompletionsStubAgentTool("aevatar_invoke_team", "Invoke a team")]));
        });

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapChatCompletionsApiEndpoints();
        await app.StartAsync();

        var response = await app.GetTestClient().PostAsync(
            "/v1/chat/completions",
            JsonContent("""{"model":"gpt-4o-mini","messages":[{"role":"user","content":"x"}]}"""));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("authentication_required");
        provider.LastRequest.Should().BeNull();

        await app.StopAsync();
    }

    [Fact]
    public async Task PostChatCompletions_WhenResponsesToolProviderRegistered_ShouldInjectSharedAevatarTools()
    {
        var provider = new ChatCompletionsRecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk
                {
                    DeltaContent = "Hi",
                    IsLast = true,
                    Usage = new TokenUsage(1, 1, 2),
                },
            ],
        };
        var toolProvider = new ChatCompletionsRecordingResponsesToolProvider(
            substituteTools: [new ChatCompletionsStubAgentTool("WebSearch", "would substitute client WebSearch")],
            additiveTools:
            [
                new ChatCompletionsStubAgentTool("use_skill", "load a skill"),
                new ChatCompletionsStubAgentTool("ornn_search_skills", "search Ornn skills"),
            ]);
        await using var app = await CreateAppAsync(provider, responsesToolProvider: toolProvider);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent("""
            {
              "model": "gpt-4o-mini",
              "messages": [{"role": "user", "content": "ping"}],
              "tools": [
                {
                  "type": "function",
                  "function": {
                    "name": "WebSearch",
                    "description": "client declared search",
                    "parameters": {"type":"object","properties":{}}
                  }
                }
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "openai-bearer");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        provider.LastRequest.Should().NotBeNull();
        var toolNames = provider.LastRequest!.Tools?.Select(static tool => tool.Name).ToArray() ?? [];
        toolNames.Should().Contain(["use_skill", "ornn_search_skills", "WebSearch"]);
    }

    private static async Task<WebApplication> CreateAppAsync(
        ChatCompletionsRecordingLLMProvider provider,
        ChatCompletionsRecordingSessionStore? sessions = null,
        IResponsesCallerScopeResolver? callerScopeResolver = null,
        IChatRoutePolicyQueryPort? chatRoutePolicyQueryPort = null,
        IResponsesRouteResolver? routeResolver = null,
        IResponsesToolProvider? responsesToolProvider = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ILLMProviderFactory>(provider);
        sessions ??= new ChatCompletionsRecordingSessionStore();
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton<ILlmSessionRegistrationPort>(sessions);
        builder.Services.AddSingleton<IResponsesCompletionApplicationService, ResponsesCompletionApplicationService>();
        builder.Services.AddSingleton(callerScopeResolver ?? new ChatCompletionsStubCallerScopeResolver());
        builder.Services.AddSingleton(chatRoutePolicyQueryPort ?? ChatCompletionsStaticChatRoutePolicyQueryPort.ForSnapshot(
            new ChatRoutePolicySnapshot(ForwardToModelAction(string.Empty), [])));
        builder.Services.AddSingleton(new ChatRouteResolver(new ChatCompletionsStaticChatRouteFallbackProvider(string.Empty)));
        builder.Services.AddSingleton(routeResolver ?? (IResponsesRouteResolver)new ChatCompletionsNoopRouteResolver());
        builder.Services.AddSingleton<IResponsesToolClassificationService, ResponsesToolClassificationService>();
        builder.Services.AddSingleton<IResponsesDirectToolPlanService, ResponsesDirectToolPlanService>();
        builder.Services.AddToolSetRegistry(options =>
        {
            options.AddToolSet(
                ToolSetNames.WorkspaceDefault,
                static _ => new StaticAgentToolSource([new ChatCompletionsStubAgentTool("aevatar_invoke_team", "Invoke a team")]));
        });
        if (responsesToolProvider != null)
            builder.Services.AddSingleton(responsesToolProvider);

        var app = builder.Build();
        app.MapChatCompletionsApiEndpoints();
        await app.StartAsync();
        return app;
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private sealed class ChatCompletionsRecordingLLMProvider : ILLMProvider, ILLMProviderFactory
    {
        public string Name => "chat-completions-recording";

        public LLMRequest? LastRequest { get; private set; }

        public IReadOnlyList<LLMStreamChunk> StreamChunks { get; init; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
            foreach (var chunk in StreamChunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return chunk;
                await Task.Yield();
            }
        }
    }

    private sealed class ChatCompletionsStubCallerScopeResolver : IResponsesCallerScopeResolver
    {
        public Task<ResponsesCallerScope> ResolveAsync(
            string nyxIdAccessToken,
            CancellationToken ct = default) =>
            Task.FromResult(new ResponsesCallerScope("user-1", "user-1", LlmSessionOriginKind.ApiKey));
    }

    private sealed class ChatCompletionsNoopRouteResolver : IResponsesRouteResolver
    {
        public Task<string?> ResolveRouteValueAsync(string slug, string bearerToken, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    private sealed class ChatCompletionsRecordingRouteResolver(IReadOnlyDictionary<string, string> map)
        : IResponsesRouteResolver
    {
        public List<string> ResolvedSlugs { get; } = [];

        public Task<string?> ResolveRouteValueAsync(string slug, string bearerToken, CancellationToken ct)
        {
            ResolvedSlugs.Add(slug);
            return Task.FromResult(map.TryGetValue(slug, out var value) ? value : null);
        }
    }

    private sealed class ChatCompletionsStaticChatRoutePolicyQueryPort(ChatRoutePolicySnapshot? snapshot)
        : IChatRoutePolicyQueryPort
    {
        public static ChatCompletionsStaticChatRoutePolicyQueryPort ForSnapshot(ChatRoutePolicySnapshot? snapshot) =>
            new(snapshot);

        public Task<ChatRoutePolicySnapshot?> LookupForCallerAsync(
            OwnerScope callerScope,
            CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class ChatCompletionsStaticChatRouteFallbackProvider(string modelName) : IChatRouteFallbackProvider
    {
        public ChatRouteDecision GetFallbackDecision() => new()
        {
            Action = ForwardToModelAction(modelName),
            UsedFallback = true,
            MatchedRuleId = string.Empty,
            ResolvedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
    }

    private static ChatRouteAction ForwardToModelAction(string modelName) => new()
    {
        ForwardToModel = new ForwardToModel { ModelName = modelName },
    };

    private static ChatRouteAction TeamToolHintAction(string teamId, string endpointId) => new()
    {
        ForwardToModel = new ForwardToModel
        {
            ToolSetRef = new ChatRouteToolSetRef { Name = ToolSetNames.WorkspaceDefault },
            ToolChoiceHint = new ChatRouteToolChoiceHint
            {
                ToolName = "aevatar_invoke_team",
                PrefilledArguments = new Struct
                {
                    Fields =
                    {
                        ["team_id"] = Google.Protobuf.WellKnownTypes.Value.ForString(teamId),
                        ["endpoint_id"] = Google.Protobuf.WellKnownTypes.Value.ForString(endpointId),
                    },
                },
            },
        },
    };

    private sealed class ChatCompletionsRecordingSessionStore : ILlmSessionRegistrationPort
    {
        public List<LlmSessionRecord> Registered { get; } = [];
        public List<(string ActorId, string ResponseId, LlmSessionStatus Status)> StatusUpdates { get; } = [];

        public Task<LlmSessionRegistrationResult> RegisterAsync(
            LlmSessionRecord record,
            CancellationToken ct = default)
        {
            Registered.Add(record);
            return Task.FromResult(new LlmSessionRegistrationResult(
                ActorId: $"llm-session:{record.ResponseId}",
                ResponseId: record.ResponseId));
        }

        public Task UpdateStatusAsync(
            string actorId,
            string responseId,
            LlmSessionStatus status,
            CancellationToken ct = default)
        {
            StatusUpdates.Add((actorId, responseId, status));
            return Task.CompletedTask;
        }

        public Task RecordForwardedToolCallAsync(
            string sessionActorId,
            string responseId,
            LlmSessionForwardedToolCall call,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task RecordCompletionAsync(
            string sessionActorId,
            string responseId,
            LlmSessionCompletion completion,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task ReceiveForwardedToolResultAsync(
            string sessionActorId,
            string responseId,
            string callId,
            string schemaHash,
            string resultJson,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task ResolveForwardedToolResultAsync(
            string sessionActorId,
            string responseId,
            string callId,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ChatCompletionsRecordingResponsesToolProvider : IResponsesToolProvider
    {
        private readonly IReadOnlyList<IAgentTool> _substituteTools;
        private readonly IReadOnlyList<IAgentTool> _additiveTools;

        public ChatCompletionsRecordingResponsesToolProvider(
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

    private sealed class StaticAgentToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class ChatCompletionsStubAgentTool : IAgentTool
    {
        public ChatCompletionsStubAgentTool(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public string Name { get; }

        public string Description { get; }

        public string ParametersSchema => """{"type":"object","properties":{}}""";

        public bool IsReadOnly => true;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }
}
