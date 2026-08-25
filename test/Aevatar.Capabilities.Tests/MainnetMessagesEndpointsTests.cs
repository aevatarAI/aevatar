using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.CQRS.Core.Abstractions.Streaming;
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

namespace Aevatar.Capabilities.Tests;

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
        request.Headers.Add(ResponsesApiEndpoints.NyxIdIdentityTokenHeader, "messages-identity-token");
        request.Headers.Add(ResponsesApiEndpoints.NyxIdDelegationTokenHeader, "messages-delegation-token");

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
        sessions.RecordedCompletions.Should().BeEmpty();
        (await sessions.GetByResponseIdAsync(root.GetProperty("id").GetString()!))!
            .Completion.Should().BeNull();

        // System message + user message both flow into the intermediate ChatMessage list.
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Messages.Should().HaveCount(2);
        provider.LastRequest.Messages[0].Role.Should().Be("system");
        provider.LastRequest.Messages[0].Content.Should().Be("You are concise.");
        provider.LastRequest.Messages[1].Role.Should().Be("user");
        provider.LastRequest.Messages[1].Content.Should().Be("Hello");
        provider.LastRequest.MaxTokens.Should().Be(256);
        // Bearer goes on the typed CallerContext, not Metadata, per PR #625 round-2 fix.
        provider.LastRequest.CallerContext!.Credentials!.NyxIdBearer.Should().Be("anthropic-bearer");
        provider.LastRequest.Metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        var callerScopeResolver = app.Services.GetRequiredService<IResponsesCallerScopeResolver>()
            .Should()
            .BeOfType<MessagesStubCallerScopeResolver>()
            .Subject;
        callerScopeResolver.LastContext.Should().Be(new ResponsesCallerScopeResolutionContext(
            "anthropic-bearer",
            "messages-identity-token",
            "messages-delegation-token"));
    }

    [Fact]
    public async Task PostMessages_WhenCompletionReadModelLags_ShouldWaitAndReturnAnthropicMessageEnvelope()
    {
        var provider = new MessagesRecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk
                {
                    DeltaContent = "Eventually visible",
                    IsLast = true,
                },
            ],
        };
        var sessions = new MessagesRecordingSessionStore
        {
            CompletionObservationLagReads = 1,
        };
        await using var app = await CreateAppAsync(provider, sessions);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent("""
            {
              "model": "claude-haiku-4-5",
              "max_tokens": 256,
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
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("content").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("stop_reason").ValueKind.Should().Be(JsonValueKind.Null);
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
        body.Should().Contain("event: content_block_start");
        body.Should().Contain("\"content_block\":{\"type\":\"text\"");
        body.Should().Contain("event: content_block_delta");
        body.Should().Contain("\"text\":\"Hel\"");
        body.Should().Contain("\"text\":\"lo\"");
        body.Should().Contain("event: content_block_stop");
        body.Should().Contain("event: message_delta");
        body.Should().Contain("\"stop_reason\":\"end_turn\"");
        body.Should().Contain("event: message_stop");
        body.Should().NotContain("stream-bearer");

        sessions.RecordedCompletions.Should().BeEmpty();
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
        provider.LastRequest!.Tools.Should().NotBeNull();
        provider.LastRequest.Tools!.Select(static tool => tool.Name)
            .Should().Equal("get_weather");
        var tool = provider.LastRequest.Tools.Single(static tool => tool.Name == "get_weather");
        tool.Description.Should().Be("Look up the weather.");
        tool.ParametersSchema.Should().Contain("\"city\"");
        tool.IsReadOnly.Should().BeTrue();
        var command = app.Services.GetRequiredService<MessagesRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ToolSelection.OwnedToolNames.Should().BeEmpty();
        command.ToolSelection.OwnedCatalogProof.ToolCount.Should().Be(0);
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
    public async Task PostMessages_WhenResponsesToolProviderRegisteredWithoutReviewedCatalog_ShouldNotAutoInjectTools()
    {
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
                new MessagesStubAgentTool("ornn_publish_skill", "would inject ornn publish bridge"),
            ]);
        await using var app = await CreateAppAsync(provider, responsesToolProvider: toolProvider);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent("""
            {
              "model": "claude-haiku-4-5",
              "max_tokens": 32,
              "messages": [{"role": "user", "content": "ping"}],
              "tools": [
                {
                  "name": "WebSearch",
                  "description": "client declared search",
                  "input_schema": {"type":"object","properties":{}}
                }
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "anthropic-bearer");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        provider.LastRequest.Should().NotBeNull();
        var toolNames = provider.LastRequest!.Tools?.Select(static tool => tool.Name).ToArray() ?? [];
        toolNames.Should().Equal("WebSearch");
        var command = app.Services.GetRequiredService<MessagesRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ToolSelection.ForwardedTools.Should().ContainSingle(tool => tool.ToolName == "WebSearch");
        command.ToolSelection.SubstitutedToolNames.Should().BeEmpty();
        command.ToolSelection.AdditiveToolNames.Should().BeEmpty();
        command.ToolSelection.OwnedToolNames.Should().BeEmpty();
        command.ToolSelection.OwnedCatalogProof.ToolCount.Should().Be(0);
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
        var routeResolver = new MessagesRecordingRouteResolver(new Dictionary<string, LLMRouteTarget>(StringComparer.Ordinal)
        {
            ["anthropic"] = new()
            {
                CatalogServiceId = "catalog-anthropic",
                ServiceSlugSnapshot = "anthropic",
            },
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
    public async Task PostMessages_WhenChatRoutePinsGAgentTool_RoutesThroughToolDrivenModelAction()
    {
        var provider = new MessagesRecordingLLMProvider
        {
            StreamChunks =
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_gagent_1",
                        Name = "aevatar_invoke_gagent",
                        ArgumentsJson = """{"actor_id":"target-agent"}""",
                    },
                    IsLast = true,
                },
            ],
        };
        var queryPort = MessagesStaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            GAgentToolHintAction("target-agent"),
            []));
        await using var app = await CreateAppAsync(
            provider,
            chatRoutePolicyQueryPort: queryPort,
            ownedToolCatalogPlanner: new FixedResponsesOwnedToolCatalogPlanner(
                ToolSetNames.WorkspaceDefault,
                new MessagesStubAgentTool("aevatar_invoke_gagent", "Invoke a GAgent")));
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
        provider.LastRequest!.Tools.Should().NotBeNull();
        provider.LastRequest.Tools!.Select(static tool => tool.Name)
            .Should().Contain("aevatar_invoke_gagent");
        provider.LastRequest.Model.Should().Be("original-claude");
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("content").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("stop_reason").ValueKind.Should().Be(JsonValueKind.Null);
        body.Should().NotContain("\"type\":\"tool_use\"");
        body.Should().NotContain("call_gagent_1");
        var command = app.Services.GetRequiredService<MessagesRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ToolSelection.ToolChoiceHintName.Should().Be("aevatar_invoke_gagent");
        command.ToolSelection.ToolChoiceHintArgumentsJson.Should().Contain("\"actor_id\":\"target-agent\"");
    }

    // ----- Test fixtures -------------------------------------------------------

    private static async Task<WebApplication> CreateAppAsync(
        MessagesRecordingLLMProvider provider,
        MessagesRecordingSessionStore? sessions = null,
        IResponsesCallerScopeResolver? callerScopeResolver = null,
        IResponsesToolProvider? responsesToolProvider = null,
        IChatRoutePolicyQueryPort? chatRoutePolicyQueryPort = null,
        IResponsesRouteResolver? routeResolver = null,
        IResponsesOwnedToolCatalogPlanner? ownedToolCatalogPlanner = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(provider);
        builder.Services.AddSingleton<ILLMProviderFactory>(provider);
        sessions ??= new MessagesRecordingSessionStore();
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton<ILlmSessionRegistrationPort>(sessions);
        builder.Services.AddSingleton<ILlmSessionQueryPort>(sessions);
        builder.Services.AddSingleton<MessagesObservationRuntime>();
        builder.Services.AddSingleton<MessagesRecordingActorDispatchPort>();
        builder.Services.AddSingleton<IActorDispatchPort>(static sp => sp.GetRequiredService<MessagesRecordingActorDispatchPort>());
        builder.Services.AddSingleton<ILlmSessionObservationScopeLeasePreparationPort>(static sp => sp.GetRequiredService<MessagesObservationRuntime>().ScopePreparationPort);
        builder.Services.AddSingleton<ILlmSessionObservationProjectionPort>(static sp => sp.GetRequiredService<MessagesObservationRuntime>().ProjectionPort);
        builder.Services.AddSingleton<ILlmSessionRunObservationService, LlmSessionRunObservationService>();
        builder.Services.AddSingleton<IMessagesCommandFacade, MessagesCommandFacade>();
        builder.Services.AddSingleton<IResponsesToolClassificationService, ResponsesToolClassificationService>();
        builder.Services.AddSingleton<IResponsesDirectToolPlanService, ResponsesDirectToolPlanService>();
        builder.Services.AddSingleton(callerScopeResolver ?? new MessagesStubCallerScopeResolver());
        builder.Services.AddSingleton(chatRoutePolicyQueryPort ?? MessagesStaticChatRoutePolicyQueryPort.ForSnapshot(
            new ChatRoutePolicySnapshot(ForwardToModelAction(string.Empty), [])));
        builder.Services.AddSingleton(new ChatRouteResolver(
            new MessagesStaticChatRouteFallbackProvider(string.Empty),
            DefaultToolSetRoutingOptions()));
        builder.Services.AddSingleton<IResponsesChatRouteDecisionPort, ResponsesChatRouteDecisionPort>();
        builder.Services.AddSingleton(routeResolver ?? (IResponsesRouteResolver)new MessagesNoopRouteResolver());
        builder.Services.AddToolSetRegistry(options =>
        {
            options.AddToolSet(
                ToolSetNames.WorkspaceDefault,
                static _ => new StaticAgentToolSource([new MessagesStubAgentTool("aevatar_invoke_gagent", "Invoke a GAgent")]));
        });
        if (responsesToolProvider != null)
            builder.Services.AddSingleton(responsesToolProvider);
        if (ownedToolCatalogPlanner != null)
            builder.Services.AddSingleton(ownedToolCatalogPlanner);

        var app = builder.Build();
        app.MapMessagesApiEndpoints();
        await app.StartAsync();
        return app;
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private sealed class MessagesRecordingActorDispatchPort(
        MessagesRecordingLLMProvider provider,
        MessagesObservationRuntime observationRuntime) : IActorDispatchPort
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

    private sealed class MessagesObservationRuntime
    {
        public MessagesObservationRuntime()
        {
            ScopePreparationPort = new MessagesObservationScopeLeasePreparationPort();
            ProjectionPort = new MessagesObservationProjectionPort();
        }

        public MessagesObservationScopeLeasePreparationPort ScopePreparationPort { get; }

        public MessagesObservationProjectionPort ProjectionPort { get; }

        public async Task PublishFromProviderAsync(
            LlmRunRequested command,
            MessagesRecordingLLMProvider provider,
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

    private sealed class MessagesObservationScopeLeasePreparationPort
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

    private sealed class MessagesObservationProjectionPort : ILlmSessionObservationProjectionPort
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
                    new MessagesObservationLease(actorId, responseId),
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

    private sealed record MessagesObservationLease(string ActorId, string ResponseId)
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

    private sealed class MessagesRecordingLLMProvider : ILLMProvider, ILLMProviderFactory
    {
        public string Name => "messages-recording";

        public LLMRequest? LastRequest { get; private set; }

        public int StreamCallCount { get; private set; }

        public IReadOnlyList<LLMStreamChunk> StreamChunks { get; init; } = [];

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public Task<IReadOnlyList<LLMStreamChunk>> CollectForCommandAsync(
            LlmRunRequested command,
            CancellationToken ct = default)
        {
            CaptureDispatchedCommand(command);
            StreamCallCount++;
            return Task.FromResult<IReadOnlyList<LLMStreamChunk>>(StreamChunks);
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
            foreach (var chunk in StreamChunks)
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
                    new MessagesStubAgentTool(tool.ToolName, tool.Description, tool.ParametersJson)));
            tools.AddRange(selection.SubstitutedToolNames.Select(static name =>
                new MessagesStubAgentTool(name, $"{name} substitute")));
            tools.AddRange(selection.AdditiveToolNames.Select(static name =>
                new MessagesStubAgentTool(name, $"{name} additive")));
            return tools
                .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
                .Select(static group => group.First())
                .ToArray();
        }
    }

    private sealed class MessagesStubCallerScopeResolver : IResponsesCallerScopeResolver
    {
        public ResponsesCallerScopeResolutionContext? LastContext { get; private set; }

        public Task<ResponsesCallerScope> ResolveAsync(
            ResponsesCallerScopeResolutionContext context,
            CancellationToken ct = default)
        {
            LastContext = context;
            return Task.FromResult(new ResponsesCallerScope("user-1", "user-1", LlmSessionOriginKind.ApiKey));
        }
    }

    private sealed class MessagesNoopRouteResolver : IResponsesRouteResolver
    {
        public Task<LLMRouteTarget?> ResolveRouteTargetAsync(
            string serviceSlug,
            string upstreamModelId,
            ResponsesCallerScope callerScope,
            CancellationToken ct) =>
            Task.FromResult<LLMRouteTarget?>(null);
    }

    private sealed class MessagesRecordingRouteResolver(IReadOnlyDictionary<string, LLMRouteTarget> map)
        : IResponsesRouteResolver
    {
        public List<string> ResolvedSlugs { get; } = [];

        public Task<LLMRouteTarget?> ResolveRouteTargetAsync(
            string serviceSlug,
            string upstreamModelId,
            ResponsesCallerScope callerScope,
            CancellationToken ct)
        {
            ResolvedSlugs.Add(serviceSlug);
            return Task.FromResult(
                map.TryGetValue(serviceSlug, out var value) ? value.Clone() : null);
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

    private static ChatRouteAction GAgentToolHintAction(string actorId) => new()
    {
        ForwardToModel = new ForwardToModel
        {
            ToolSetRef = new ChatRouteToolSetRef { Name = ToolSetNames.WorkspaceDefault },
            ToolChoiceHint = new ChatRouteToolChoiceHint
            {
                ToolName = "aevatar_invoke_gagent",
                PrefilledArguments = new Struct
                {
                    Fields =
                    {
                        ["actor_id"] = Google.Protobuf.WellKnownTypes.Value.ForString(actorId),
                    },
                },
            },
        },
    };

    private sealed class MessagesRecordingSessionStore :
        ILlmSessionRegistrationPort,
        ILlmSessionQueryPort
    {
        private readonly Dictionary<string, LlmSessionSnapshot> _snapshots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _completionObservationLagReads = new(StringComparer.Ordinal);

        public List<LlmSessionRecord> Registered { get; } = [];
        public List<(string ActorId, string ResponseId, LlmSessionStatus Status)> StatusUpdates { get; } = [];
        public List<(string ActorId, string ResponseId, LlmSessionCompletion Completion)> RecordedCompletions { get; } = [];

        public int CompletionObservationLagReads { get; init; }

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

        public Task CancelRunAsync(
            string sessionActorId,
            string responseId,
            string runId,
            CancellationToken ct = default) =>
            Task.CompletedTask;

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
            CancellationToken ct = default) => Task.CompletedTask;

        public Task ResolveForwardedToolResultAsync(
            string sessionActorId,
            string responseId,
            string callId,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<LlmSessionSnapshot?> GetByResponseIdAsync(
            string responseId,
            CancellationToken ct = default)
        {
            var snapshot = _snapshots.GetValueOrDefault(responseId);
            if (snapshot?.Completion is not null &&
                _completionObservationLagReads.TryGetValue(responseId, out var remaining) &&
                remaining > 0)
            {
                _completionObservationLagReads[responseId] = remaining - 1;
                return Task.FromResult<LlmSessionSnapshot?>(snapshot with { Completion = null });
            }

            return Task.FromResult(snapshot);
        }
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

    private sealed class StaticAgentToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class MessagesStubAgentTool : IAgentTool
    {
        public MessagesStubAgentTool(
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

        public bool IsReadOnly => true;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }
}
