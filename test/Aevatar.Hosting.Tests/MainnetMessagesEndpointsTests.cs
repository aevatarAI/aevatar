using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.Mainnet.Host.Api.Messages;
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

/// <summary>
/// Path B (Anthropic Messages, <c>/v1/messages</c>) smoke tests. Path B is a stateless
/// facade — it shares the LlmSessionGAgent / NyxIdLLMProvider / completion service
/// pipeline with /v1/responses, so we only assert the contract pieces unique to the
/// Anthropic surface here (request shape, response shape, SSE frame schedule).
/// </summary>
public sealed class MainnetMessagesEndpointsTests
{
    [Fact]
    public async Task PostMessages_NonStreaming_ShouldReturnAnthropicMessageEnvelope()
    {
        var provider = new MessagesRecordingLLMProvider
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
        var sessions = new MessagesRecordingSessionStore();
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent("""
            {
              "model": "claude-haiku-4-5",
              "max_tokens": 256,
              "system": "You are concise.",
              "messages": [
                {"role": "user", "content": "Hello"}
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "anthropic-bearer");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain("anthropic-bearer");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("id").GetString().Should().StartWith("msg_");
        root.GetProperty("type").GetString().Should().Be("message");
        root.GetProperty("role").GetString().Should().Be("assistant");
        root.GetProperty("model").GetString().Should().Be("claude-haiku-4-5");
        root.GetProperty("stop_reason").ValueKind.Should().Be(JsonValueKind.Null);
        var content = root.GetProperty("content");
        content.GetArrayLength().Should().Be(0);
        root.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(0);
        root.GetProperty("usage").GetProperty("output_tokens").GetInt32().Should().Be(0);

        // Path B reuses the same LlmSession actor as Path A (no MessagesSessionGAgent).
        sessions.Registered.Should().ContainSingle();
        sessions.Registered[0].ScopeId.Should().Be("user-1");
        sessions.RecordedCompletions.Should().ContainSingle()
            .Which.Completion.OutputText.Should().Be("Hi there");
        provider.LastRequest.Should().NotBeNull();
    }

    [Fact]
    public async Task PostMessages_Streaming_ShouldEmitAnthropicSseFrames()
    {
        var provider = new MessagesRecordingLLMProvider
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
        var sessions = new MessagesRecordingSessionStore();
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent("""
            {
              "model": "claude-haiku-4-5",
              "max_tokens": 64,
              "messages": [{"role": "user", "content": "ping"}],
              "stream": true
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "stream-bearer");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        body.Should().Contain("event: message_start");
        body.Should().Contain("\"type\":\"message_start\"");
        body.Should().NotContain("event: content_block_start");
        body.Should().NotContain("event: content_block_delta");
        body.Should().NotContain("\"text\":\"Hello\"");
        body.Should().Contain("event: message_delta");
        body.Should().Contain("\"stop_reason\":null");
        body.Should().Contain("event: message_stop");
        body.Should().NotContain("stream-bearer");

        sessions.RecordedCompletions.Should().ContainSingle()
            .Which.Completion.OutputText.Should().Be("Hello");
    }

    [Fact]
    public async Task PostMessages_WithToolCall_ShouldEmitToolUseContentBlock()
    {
        var provider = new MessagesRecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "toolu_abc",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":"SF"}""",
                    },
                    IsLast = true,
                },
            ],
        };
        var sessions = new MessagesRecordingSessionStore();
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent("""
            {
              "model": "claude-haiku-4-5",
              "max_tokens": 256,
              "messages": [{"role": "user", "content": "weather in SF"}],
              "tools": [
                {
                  "name": "get_weather",
                  "description": "Look up the weather.",
                  "input_schema": {"type":"object","properties":{"city":{"type":"string"}}}
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
        var root = doc.RootElement;
        root.GetProperty("stop_reason").ValueKind.Should().Be(JsonValueKind.Null);
        var content = root.GetProperty("content");
        content.GetArrayLength().Should().Be(0);

        provider.LastRequest.Should().NotBeNull();
        var tool = provider.LastRequest!.Tools.Should().ContainSingle().Subject;
        tool.Name.Should().Be("get_weather");
        tool.Description.Should().Be("Look up the weather.");
        tool.ParametersSchema.Should().Contain("\"city\"");
        tool.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public async Task PostMessages_WithoutBearer_ShouldReturn401WithAnthropicErrorEnvelope()
    {
        var provider = new MessagesRecordingLLMProvider();
        await using var app = await CreateAppAsync(provider);
        var client = app.GetTestClient();

        var response = await client.PostAsync(
            "/v1/messages",
            JsonContent("""{"model":"claude-haiku-4-5","max_tokens":1,"messages":[{"role":"user","content":"x"}]}"""));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("type").GetString().Should().Be("error");
        doc.RootElement.GetProperty("error").GetProperty("type").GetString().Should().Be("authentication_error");
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostMessages_WithNonBearerAuthorization_ShouldReturn401()
    {
        var provider = new MessagesRecordingLLMProvider();
        await using var app = await CreateAppAsync(provider);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent("""{"model":"claude-haiku-4-5","max_tokens":1,"messages":[{"role":"user","content":"x"}]}"""),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "not-a-bearer");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostMessages_WithToolResultBlockInUserContent_ShouldFlattenIntoToolRoleMessage()
    {
        // Anthropic Messages multi-turn tool flow: the *next* user message carries a
        // tool_result content block with the prior tool's output. Path B must replay
        // that as a role=tool ChatMessage so the OpenAI-shaped intermediate doesn't
        // drop the tool result.
        var provider = new MessagesRecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "OK", IsLast = true, Usage = new TokenUsage(2, 1, 3) },
            ],
        };
        var sessions = new MessagesRecordingSessionStore();
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent("""
            {
              "model": "claude-haiku-4-5",
              "max_tokens": 64,
              "messages": [
                {"role": "user", "content": "weather?"},
                {"role": "assistant", "content": [
                  {"type": "tool_use", "id": "toolu_x", "name": "get_weather", "input": {"city":"SF"}}
                ]},
                {"role": "user", "content": [
                  {"type": "tool_result", "tool_use_id": "toolu_x", "content": "sunny"}
                ]}
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "multi-turn-bearer");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        provider.LastRequest.Should().NotBeNull();
        var messages = provider.LastRequest!.Messages;
        messages.Should().HaveCount(3);
        messages[0].Role.Should().Be("user");
        messages[1].Role.Should().Be("assistant");
        messages[1].ToolCalls.Should().ContainSingle().Which.Name.Should().Be("get_weather");
        messages[2].Role.Should().Be("tool");
        messages[2].ToolCallId.Should().Be("toolu_x");
        messages[2].Content.Should().Be("sunny");
    }

    [Fact]
    public async Task PostMessages_WithThinkingBlock_ShouldPreserveAssistantReasoning()
    {
        var provider = new MessagesRecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "OK", IsLast = true },
            ],
        };
        await using var app = await CreateAppAsync(provider);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent("""
            {
              "model": "claude-haiku-4-5",
              "max_tokens": 64,
              "messages": [
                {"role": "user", "content": "2+2?"},
                {"role": "assistant", "content": [
                  {"type": "thinking", "thinking": "Need simple arithmetic."},
                  {"type": "text", "text": "4"}
                ]},
                {"role": "user", "content": "thanks"}
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "thinking-bearer");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        provider.LastRequest.Should().NotBeNull();
        var assistant = provider.LastRequest!.Messages.Should().Contain(m => m.Role == "assistant").Subject;
        assistant.Content.Should().Be("4");
        assistant.ReasoningContent.Should().Be("Need simple arithmetic.");
    }

    [Theory]
    [InlineData("""{"top_p":0.5}""")]
    [InlineData("""{"top_k":10}""")]
    [InlineData("""{"stop_sequences":["END"]}""")]
    [InlineData("""{"tool_choice":{"type":"any"}}""")]
    public async Task PostMessages_WithUnsupportedControlParameter_ShouldReturn400(string extraJson)
    {
        var provider = new MessagesRecordingLLMProvider();
        await using var app = await CreateAppAsync(provider);
        var client = app.GetTestClient();
        var extra = JsonDocument.Parse(extraJson).RootElement.EnumerateObject().Single();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent($$"""
            {
              "model": "claude-haiku-4-5",
              "max_tokens": 64,
              "messages": [{"role": "user", "content": "ping"}],
              "{{extra.Name}}": {{extra.Value.GetRawText()}}
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unsupported-bearer");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("type").GetString().Should().Be("error");
        doc.RootElement.GetProperty("error").GetProperty("type").GetString().Should().Be("unsupported_parameter");
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostMessages_WhenResponsesToolProviderRegistered_ShouldNotInjectAevatarAdditiveTools()
    {
        // Regression: /v1/messages must explicitly pass Array.Empty<IResponsesToolProvider>()
        // to ResponsesToolClassifier so Aevatar substitutes/additives never shadow the
        // Anthropic client's own tool harness (Claude Code in particular). If a future
        // refactor wires DI providers into this path, this test fails.
        var provider = new MessagesRecordingLLMProvider
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
        var toolProvider = new MessagesRecordingResponsesToolProvider(
            substituteTools: [new MessagesStubAgentTool("WebSearch", "would substitute client WebSearch")],
            additiveTools: [
                new MessagesStubAgentTool("use_skill", "would inject skill bridge"),
                new MessagesStubAgentTool("ornn_search_skills", "would inject ornn bridge"),
            ]);
        await using var app = await CreateAppAsync(provider, responsesToolProvider: toolProvider);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent("""
            {
              "model": "claude-haiku-4-5",
              "max_tokens": 32,
              "messages": [{"role": "user", "content": "ping"}]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "anthropic-bearer");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        provider.LastRequest.Should().NotBeNull();
        var toolNames = provider.LastRequest!.Tools?.Select(static tool => tool.Name).ToArray() ?? [];
        toolNames.Should().NotContain(["use_skill", "ornn_search_skills", "WebSearch"]);
    }

    [Fact]
    public async Task PostMessages_WhenChatRouteForwardsToModel_RewritesModelBeforeCompletionService()
    {
        var provider = new MessagesRecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "Hi", IsLast = true, Usage = new TokenUsage(1, 1, 2) },
            ],
        };
        var queryPort = MessagesStaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            ForwardToModelAction("routed-claude"),
            []));
        await using var app = await CreateAppAsync(provider, chatRoutePolicyQueryPort: queryPort);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent("""
            {
              "model": "original-claude",
              "max_tokens": 32,
              "messages": [{"role": "user", "content": "ping"}]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "anthropic-bearer");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Model.Should().Be("routed-claude");
    }

    [Fact]
    public async Task PostMessages_WhenChatRouteMatchesModelAndDeclaredTools_UsesRuleAction()
    {
        var provider = new MessagesRecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "Hi", IsLast = true, Usage = new TokenUsage(1, 1, 2) },
            ],
        };
        var queryPort = MessagesStaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            ForwardToModelAction("default-claude"),
            [
                new ChatRouteRule
                {
                    RuleId = "messages-model-tools",
                    Priority = 10,
                    Match = new ChatRouteMatch
                    {
                        SourceKind = ChatSourceKind.NyxResponses,
                        Model = "original-claude",
                        ToolMode = ToolMode.Declared,
                    },
                    Action = ForwardToModelAction("routed-tool-claude"),
                },
            ]));
        await using var app = await CreateAppAsync(provider, chatRoutePolicyQueryPort: queryPort);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent("""
            {
              "model": "original-claude",
              "max_tokens": 32,
              "messages": [{"role": "user", "content": "ping"}],
              "tools": [{"name":"do_thing","description":"do a thing","input_schema":{"type":"object","properties":{}}}]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "anthropic-bearer");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Model.Should().Be("routed-tool-claude");
    }

    [Fact]
    public async Task PostMessages_WhenBareClaudeModel_AutoPrefixesAnthropicAndResolvesRoute()
    {
        // Regression: cc-switch / Claude Code / Anthropic SDK send raw model
        // ids without provider prefix (e.g. `claude-sonnet-4-5-20250929`).
        // Without auto-prefix the catalog router treats them as gateway-default
        // and NyxID upstream rejects with HTTP 400. /v1/messages must inject
        // `anthropic/` so the existing route resolver finds the anthropic
        // backend, then strip the prefix back off before sending to the LLM
        // provider so the bare model reaches the upstream verbatim.
        var provider = new MessagesRecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "ok", IsLast = true, Usage = new TokenUsage(1, 1, 2) },
            ],
        };
        var routeResolver = new MessagesRecordingRouteResolver(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["anthropic"] = "/api/v1/llm/anthropic/v1",
        });
        await using var app = await CreateAppAsync(provider, routeResolver: routeResolver);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent("""
            {
              "model": "claude-sonnet-4-5-20250929",
              "max_tokens": 16,
              "messages": [{"role": "user", "content": "ping"}]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "anthropic-bearer");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        routeResolver.ResolvedSlugs.Should().ContainSingle()
            .Which.Should().Be("anthropic", "the synthetic `anthropic/` prefix must reach the route resolver");
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Model.Should().Be(
            "claude-sonnet-4-5-20250929",
            "the prefix is a routing artifact only — the LLM provider must see the bare anthropic model id");
    }

    [Fact]
    public async Task PostMessages_WhenBareClaudeModelAndResolverUnknown_FallsBackToOriginalBareModel()
    {
        // Defense in depth: when the route resolver doesn't recognize the
        // synthesized "anthropic" slug (e.g. catalog hasn't loaded yet, or a
        // future deploy renames the route), the prefix injection must not
        // make things worse than the pre-fix behavior. Provider should still
        // see the original bare model so the request reaches the gateway with
        // the same string the client sent.
        var provider = new MessagesRecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk { DeltaContent = "ok", IsLast = true, Usage = new TokenUsage(1, 1, 2) },
            ],
        };
        await using var app = await CreateAppAsync(provider); // MessagesNoopRouteResolver
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent("""
            {
              "model": "claude-sonnet-4-5-20250929",
              "max_tokens": 16,
              "messages": [{"role": "user", "content": "ping"}]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "anthropic-bearer");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        provider.LastRequest!.Model.Should().Be(
            "claude-sonnet-4-5-20250929",
            "when the resolver doesn't know `anthropic`, fall back to the pre-fix behavior verbatim");
    }

    [Fact]
    public async Task PostMessages_WhenChatRouteRejects_ReturnsForbiddenWithoutLlmCall()
    {
        var provider = new MessagesRecordingLLMProvider();
        var queryPort = MessagesStaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            RejectAction("policy_denied", "blocked by policy"),
            []));
        await using var app = await CreateAppAsync(provider, chatRoutePolicyQueryPort: queryPort);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent("""
            {
              "model": "original-claude",
              "max_tokens": 32,
              "messages": [{"role": "user", "content": "ping"}]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "anthropic-bearer");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("error").GetProperty("type").GetString().Should().Be("chat_route_rejected");
        doc.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Be("blocked by policy");
        provider.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PostMessages_WhenChatRouteForwardsToGAgent_ReturnsNotImplementedWithoutLlmCall()
    {
        var provider = new MessagesRecordingLLMProvider();
        var queryPort = MessagesStaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            ForwardToGAgentAction("target-agent"),
            []));
        await using var app = await CreateAppAsync(provider, chatRoutePolicyQueryPort: queryPort);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent("""
            {
              "model": "original-claude",
              "max_tokens": 32,
              "messages": [{"role": "user", "content": "ping"}]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "anthropic-bearer");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("error").GetProperty("type").GetString()
            .Should().Be("chat_route_action_not_supported");
        provider.LastRequest.Should().BeNull();
    }

    // ----- Test fixtures -------------------------------------------------------

    private static async Task<WebApplication> CreateAppAsync(
        MessagesRecordingLLMProvider provider,
        MessagesRecordingSessionStore? sessions = null,
        IResponsesCallerScopeResolver? callerScopeResolver = null,
        IResponsesToolProvider? responsesToolProvider = null,
        IChatRoutePolicyQueryPort? chatRoutePolicyQueryPort = null,
        IResponsesRouteResolver? routeResolver = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ILLMProviderFactory>(provider);
        sessions ??= new MessagesRecordingSessionStore();
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton<ILlmSessionRegistrationPort>(sessions);
        builder.Services.AddSingleton<ILlmSessionQueryPort>(sessions);
        builder.Services.AddSingleton<IActorDispatchPort>(sp => new MessagesRecordingLlmRunDispatchPort(
            provider,
            sessions,
            sp.GetServices<IResponsesToolProvider>()));
        builder.Services.AddSingleton<IMessagesCommandFacade, MessagesCommandFacade>();
        builder.Services.AddSingleton(callerScopeResolver ?? new MessagesStubCallerScopeResolver());
        builder.Services.AddSingleton(chatRoutePolicyQueryPort ?? MessagesStaticChatRoutePolicyQueryPort.ForSnapshot(
            new ChatRoutePolicySnapshot(ForwardToModelAction(string.Empty), [])));
        builder.Services.AddSingleton(new ChatRouteResolver(new MessagesStaticChatRouteFallbackProvider(string.Empty)));
        builder.Services.AddSingleton<IResponsesChatRouteDecisionPort, ResponsesChatRouteDecisionPort>();
        builder.Services.AddSingleton(routeResolver ?? (IResponsesRouteResolver)new MessagesNoopRouteResolver());
        if (responsesToolProvider != null)
            builder.Services.AddSingleton(responsesToolProvider);

        var app = builder.Build();
        app.MapMessagesApiEndpoints();
        await app.StartAsync();
        return app;
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private sealed class MessagesRecordingLLMProvider : ILLMProvider, ILLMProviderFactory
    {
        public string Name => "messages-recording";

        public LLMRequest? LastRequest { get; private set; }

        public int StreamCallCount { get; private set; }

        public IReadOnlyList<LLMStreamChunk> StreamChunks { get; init; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
            StreamCallCount++;
            foreach (var chunk in StreamChunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return chunk;
                await Task.Yield();
            }
        }
    }

    private sealed class MessagesStubCallerScopeResolver : IResponsesCallerScopeResolver
    {
        public Task<ResponsesCallerScope> ResolveAsync(
            string nyxIdAccessToken,
            CancellationToken ct = default) =>
            Task.FromResult(new ResponsesCallerScope("user-1", "user-1", LlmSessionOriginKind.ApiKey));
    }

    private sealed class MessagesNoopRouteResolver : IResponsesRouteResolver
    {
        public Task<string?> ResolveRouteValueAsync(string slug, string bearerToken, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    private sealed class MessagesRecordingRouteResolver(IReadOnlyDictionary<string, string> map)
        : IResponsesRouteResolver
    {
        public List<string> ResolvedSlugs { get; } = [];

        public Task<string?> ResolveRouteValueAsync(string slug, string bearerToken, CancellationToken ct)
        {
            ResolvedSlugs.Add(slug);
            return Task.FromResult(map.TryGetValue(slug, out var value) ? value : null);
        }
    }

    private sealed class MessagesStaticChatRoutePolicyQueryPort(ChatRoutePolicySnapshot? snapshot)
        : IChatRoutePolicyQueryPort
    {
        public static MessagesStaticChatRoutePolicyQueryPort ForSnapshot(ChatRoutePolicySnapshot? snapshot) =>
            new(snapshot);

        public Task<ChatRoutePolicySnapshot?> LookupForCallerAsync(
            OwnerScope callerScope,
            CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class MessagesStaticChatRouteFallbackProvider(string modelName) : IChatRouteFallbackProvider
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

    private static ChatRouteAction ForwardToGAgentAction(string actorId) => new()
    {
        ForwardToGagent = new ForwardToGAgent { ActorId = actorId },
    };

    private sealed class MessagesRecordingSessionStore :
        ILlmSessionRegistrationPort,
        ILlmSessionQueryPort
    {
        private readonly Dictionary<string, LlmSessionSnapshot> _snapshots = new(StringComparer.Ordinal);

        public List<LlmSessionRecord> Registered { get; } = [];
        public List<(string ActorId, string ResponseId, LlmSessionStatus Status)> StatusUpdates { get; } = [];
        public List<(string ActorId, string ResponseId, LlmSessionCompletion Completion)> RecordedCompletions { get; } = [];

        public Task<LlmSessionRegistrationResult> RegisterAsync(
            LlmSessionRecord record,
            CancellationToken ct = default)
        {
            var clone = record.Clone();
            Registered.Add(clone);
            var actorId = $"llm-session:{clone.ResponseId}";
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

            return Task.CompletedTask;
        }

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

        public Task<LlmSessionSnapshot?> GetByResponseIdAsync(
            string responseId,
            CancellationToken ct = default) =>
            Task.FromResult(_snapshots.GetValueOrDefault(responseId));
    }

    private sealed class MessagesRecordingResponsesToolProvider : IResponsesToolProvider
    {
        private readonly IReadOnlyList<IAgentTool> _substituteTools;
        private readonly IReadOnlyList<IAgentTool> _additiveTools;

        public MessagesRecordingResponsesToolProvider(
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

    private sealed class MessagesRecordingLlmRunDispatchPort(
        MessagesRecordingLLMProvider provider,
        MessagesRecordingSessionStore sessions,
        IEnumerable<IResponsesToolProvider> toolProviders) : IActorDispatchPort
    {
        public async Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            var command = envelope.Payload.Unpack<LlmRunRequested>();
            var tools = await BuildEffectiveToolsAsync(command, ct);
            var outputText = new StringBuilder();
            TokenUsage? usage = null;
            var toolCalls = new TestToolCallAccumulator();

            var request = new LLMRequest
            {
                Messages = command.Messages.Select(ToChatMessage).ToList(),
                RequestId = command.ResponseId,
                Metadata = BuildRequestMetadata(command),
                CallerContext = new LLMRequestCallerContext(
                    command.ScopeId,
                    command.OwnerSubject,
                    command.ResponseId,
                    new LLMRequestCallerCredentials(command.BearerToken)),
                Tools = tools,
                LlmControl = new LLMControlContext(
                    NyxIdAccessToken: null,
                    NyxIdOrgToken: null,
                    SenderNyxIdAccessToken: null,
                    ModelOverride: null,
                    NyxIdRoutePreference: string.IsNullOrWhiteSpace(command.RoutePreference)
                        ? null
                        : command.RoutePreference,
                    MaxToolRoundsOverride: null,
                    UserMemoryPrompt: null),
                Model = string.IsNullOrWhiteSpace(command.Model) ? null : command.Model,
                Temperature = command.HasTemperature ? command.Temperature : null,
                MaxTokens = command.HasMaxTokens ? command.MaxTokens : null,
            };

            await foreach (var chunk in provider.ChatStreamAsync(request, ct))
            {
                var delta = ExtractChunkText(chunk);
                if (!string.IsNullOrEmpty(delta))
                    outputText.Append(delta);
                if (chunk.Usage is not null)
                    usage = chunk.Usage;
                if (chunk.DeltaToolCall is not null)
                    toolCalls.TrackDelta(chunk.DeltaToolCall);
                if (chunk.IsLast)
                    break;
            }

            await sessions.RecordCompletionAsync(
                actorId,
                command.ResponseId,
                BuildCompletion(outputText.ToString(), toolCalls.BuildToolCalls(), usage),
                ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }

        private async Task<IReadOnlyList<IAgentTool>> BuildEffectiveToolsAsync(
            LlmRunRequested command,
            CancellationToken ct)
        {
            var context = new ResponsesToolProviderContext(
                new ResponsesToolProviderCallerScope(command.ScopeId, command.OwnerSubject, LlmSessionOriginKind.ApiKey.ToString()),
                BuildRequestMetadata(command));
            var providers = toolProviders.ToArray();
            var substituteTools = new List<IAgentTool>();
            var additiveTools = new List<IAgentTool>();
            foreach (var toolProvider in providers)
            {
                substituteTools.AddRange(await toolProvider.GetSubstituteToolsAsync(context, ct));
                additiveTools.AddRange(await toolProvider.GetAdditiveToolsAsync(context, ct));
            }

            var substitutedNames = command.ToolSelection?.SubstitutedToolNames.ToHashSet(StringComparer.Ordinal)
                ?? [];
            var substitutesByName = substituteTools
                .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
            var effective = new List<IAgentTool>();
            foreach (var declaration in command.ToolSelection?.ForwardedTools ?? [])
            {
                if (substitutedNames.Contains(declaration.ToolName) &&
                    substitutesByName.TryGetValue(declaration.ToolName, out var substitute))
                {
                    effective.Add(substitute);
                    continue;
                }

                effective.Add(new MessagesForwardedTestAgentTool(declaration));
            }

            var names = effective.Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            var additiveNames = command.ToolSelection?.AdditiveToolNames.ToHashSet(StringComparer.Ordinal)
                ?? [];
            foreach (var additive in additiveTools)
            {
                if (additiveNames.Contains(additive.Name) && names.Add(additive.Name))
                    effective.Add(additive);
            }

            return effective;
        }

        private static Dictionary<string, string> BuildRequestMetadata(LlmRunRequested command) =>
            new(StringComparer.Ordinal)
            {
                [LLMRequestMetadataKeys.RequestId] = command.ResponseId,
                ["scope_id"] = command.ScopeId,
            };

        private static ChatMessage ToChatMessage(LlmSessionRuntimeChatMessage message) =>
            new()
            {
                Role = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role,
                Content = message.Content,
                ReasoningContent = string.IsNullOrWhiteSpace(message.ReasoningContent) ? null : message.ReasoningContent,
                ToolCallId = string.IsNullOrWhiteSpace(message.ToolCallId) ? null : message.ToolCallId,
                ToolCalls = message.ToolCalls.Count == 0
                    ? null
                    : message.ToolCalls.Select(static call => new ToolCall
                    {
                        Id = call.CallId,
                        Name = call.ToolName,
                        ArgumentsJson = call.ArgumentsJson,
                    }).ToArray(),
            };

        private static string? ExtractChunkText(LLMStreamChunk chunk)
        {
            if (!string.IsNullOrWhiteSpace(chunk.DeltaContent))
                return chunk.DeltaContent;
            return chunk.DeltaContentPart is { Kind: ContentPartKind.Text } part
                ? part.Text
                : null;
        }

        private static LlmSessionCompletion BuildCompletion(
            string outputText,
            IReadOnlyList<ToolCall> toolCalls,
            TokenUsage? usage)
        {
            var completion = new LlmSessionCompletion
            {
                OutputText = outputText,
                CompletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            };
            if (usage is not null)
            {
                completion.Usage = new LlmSessionTokenUsage
                {
                    PromptTokens = usage.PromptTokens,
                    CompletionTokens = usage.CompletionTokens,
                    TotalTokens = usage.TotalTokens,
                };
            }

            completion.ToolCalls.AddRange(toolCalls.Select(static call => new LlmSessionCompletedToolCall
            {
                CallId = call.Id,
                ToolName = call.Name,
                Result = ResponsesJsonValues.ParseBoundaryPayload(
                    string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson),
            }));
            return completion;
        }

        private sealed class TestToolCallAccumulator
        {
            private readonly Dictionary<string, (string Name, StringBuilder Arguments)> _calls = new(StringComparer.Ordinal);
            private readonly List<string> _order = [];
            private int _anonymousCounter;

            public void TrackDelta(ToolCall delta)
            {
                var id = string.IsNullOrWhiteSpace(delta.Id)
                    ? $"anonymous-{_anonymousCounter++}"
                    : delta.Id;
                if (!_calls.TryGetValue(id, out var current))
                {
                    current = (delta.Name, new StringBuilder());
                    _calls[id] = current;
                    _order.Add(id);
                }

                if (!string.IsNullOrWhiteSpace(delta.Name))
                    current.Name = delta.Name;
                if (!string.IsNullOrEmpty(delta.ArgumentsJson))
                    current.Arguments.Append(delta.ArgumentsJson);
                _calls[id] = current;
            }

            public IReadOnlyList<ToolCall> BuildToolCalls() =>
                _order.Select(id =>
                {
                    var current = _calls[id];
                    return new ToolCall
                    {
                        Id = id,
                        Name = current.Name,
                        ArgumentsJson = current.Arguments.ToString(),
                    };
                }).ToArray();
        }

        private sealed class MessagesForwardedTestAgentTool(LlmSessionRuntimeToolDeclaration declaration) : IAgentTool
        {
            public string Name { get; } = declaration.ToolName;

            public string Description { get; } = declaration.Description;

            public string ParametersSchema { get; } = declaration.ParametersJson;

            public bool IsReadOnly => true;

            public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
                throw new InvalidOperationException("Forwarded test tool must not execute locally.");
        }
    }

    private sealed class MessagesStubAgentTool : IAgentTool
    {
        public MessagesStubAgentTool(string name, string description)
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
