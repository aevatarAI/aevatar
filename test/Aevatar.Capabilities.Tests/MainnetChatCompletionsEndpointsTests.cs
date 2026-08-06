using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.Authentication.Hosting;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.Mainnet.Host.Api.ChatCompletions;
using Aevatar.Mainnet.Host.Api.Responses;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetChatCompletionsEndpointsTests
{
    [Fact]
    public async Task PostChatCompletions_NonStreaming_ShouldReturnOpenAIEnvelope()
    {
        var provider = new ChatCompletionsRecordingLLMProvider();
        var sessions = new ChatCompletionsRecordingSessionStore();
        var observation = ChatCompletionsObservationScenarioBuilder.ForText("Hi there")
            .WithUsage(5, 3, 8)
            .Build();
        await using var app = await CreateAppAsync(provider, sessions, observationRuntime: observation);
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
        request.Headers.Add(ResponsesApiEndpoints.NyxIdIdentityTokenHeader, "chat-identity-token");
        request.Headers.Add(ResponsesApiEndpoints.NyxIdDelegationTokenHeader, "chat-delegation-token");

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
        root.GetProperty("usage").GetProperty("total_tokens").GetInt32().Should().Be(8);

        sessions.Registered.Should().ContainSingle();
        sessions.Registered[0].ScopeId.Should().Be("user-1");
        sessions.StatusUpdates.Should().BeEmpty();
        provider.LastRequest.Should().BeNull();
        var dispatch = app.Services.GetRequiredService<ChatCompletionsRecordingActorDispatchPort>();
        var command = dispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Messages.Should().HaveCount(2);
        command.Messages[0].Role.Should().Be("system");
        command.Messages[0].Content.Should().Be("You are concise.");
        command.Messages[1].Role.Should().Be("user");
        command.Messages[1].Content.Should().Be("Hello");
        command.MaxTokens.Should().Be(256);
        command.BearerToken.Should().Be("openai-bearer");
        var callerScopeResolver = app.Services.GetRequiredService<IResponsesCallerScopeResolver>()
            .Should()
            .BeOfType<ChatCompletionsStubCallerScopeResolver>()
            .Subject;
        callerScopeResolver.LastContext.Should().Be(new ResponsesCallerScopeResolutionContext(
            "openai-bearer",
            "chat-identity-token",
            "chat-delegation-token"));
    }

    [Fact]
    public async Task PostChatCompletions_Streaming_ShouldEmitOpenAIChunksAndDone()
    {
        var provider = new ChatCompletionsRecordingLLMProvider();
        var sessions = new ChatCompletionsRecordingSessionStore();
        var observation = ChatCompletionsObservationScenarioBuilder.ForText("Hello")
            .WithChunkText("Hel")
            .WithChunkText("lo")
            .WithUsage(4, 2, 6)
            .Build();
        await using var app = await CreateAppAsync(provider, sessions, observationRuntime: observation);
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
        body.Should().Contain("\"finish_reason\":null");
        body.Should().Contain("\"finish_reason\":\"stop\"");
        body.Should().Contain("\"prompt_tokens\":4");
        body.Should().Contain("\"completion_tokens\":2");
        body.Should().Contain("data: [DONE]");
        body.Should().NotContain("stream-bearer");
        sessions.StatusUpdates.Should().BeEmpty();
        provider.LastRequest.Should().BeNull();
        app.Services.GetRequiredService<ChatCompletionsRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task PostChatCompletions_StreamingWithToolCalls_ShouldEmitOnlyTerminalToolCallSnapshotBeforeStop()
    {
        var provider = new ChatCompletionsRecordingLLMProvider();
        var observation = ChatCompletionsObservationScenarioBuilder.ForText(string.Empty)
            .WithChunkText("Checking")
            .WithToolCallDelta("call_1", "get_weather", """{"city":"SF"}""")
            .WithToolCallDelta("call_2", "get_time", """{"zone":"UTC"}""")
            .WithCompletedToolCall("call_1", "get_weather", """{"city":"SF"}""")
            .WithCompletedToolCall("call_2", "get_time", """{"zone":"UTC"}""")
            .Build();
        await using var app = await CreateAppAsync(provider, observationRuntime: observation);
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent("""
            {
              "model": "gpt-4o-mini",
              "messages": [{"role": "user", "content": "weather and time"}],
              "stream": true,
              "tools": [
                {
                  "type": "function",
                  "function": {
                    "name": "get_weather",
                    "parameters": {"type":"object","properties":{"city":{"type":"string"}}}
                  }
                },
                {
                  "type": "function",
                  "function": {
                    "name": "get_time",
                    "parameters": {"type":"object","properties":{"zone":{"type":"string"}}}
                  }
                }
              ]
            }
            """),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "stream-tool-bearer");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var frames = ParseDataFrames(body).ToArray();
        frames.Should().HaveCount(3);
        frames[0].RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("content")
            .GetString()
            .Should()
            .Be("Checking");
        var toolChunk = frames[1].RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls");
        toolChunk.GetArrayLength().Should().Be(2);
        toolChunk[0].GetProperty("index").GetInt32().Should().Be(0);
        toolChunk[0].GetProperty("id").GetString().Should().Be("call_1");
        toolChunk[1].GetProperty("index").GetInt32().Should().Be(1);
        toolChunk[1].GetProperty("id").GetString().Should().Be("call_2");
        frames[2].RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString()
            .Should()
            .Be("tool_calls");
        body.Should().Contain("data: [DONE]");
        body.IndexOf("\"tool_calls\":", StringComparison.Ordinal)
            .Should()
            .Be(body.LastIndexOf("\"tool_calls\":", StringComparison.Ordinal));
        body.IndexOf("\"tool_calls\":", StringComparison.Ordinal)
            .Should()
            .BeLessThan(body.IndexOf("\"finish_reason\":\"tool_calls\"", StringComparison.Ordinal));

        foreach (var frame in frames)
        {
            if (frame.RootElement.GetProperty("choices").GetArrayLength() == 0)
                continue;
            var delta = frame.RootElement.GetProperty("choices")[0].GetProperty("delta");
            if (delta.TryGetProperty("tool_calls", out var calls))
                calls.GetArrayLength().Should().Be(2);
        }
    }

    [Fact]
    public async Task PostChatCompletions_WithToolCall_ShouldDispatchForwardedToolSelection_AndReturnToolCalls()
    {
        var provider = new ChatCompletionsRecordingLLMProvider();
        var observation = ChatCompletionsObservationScenarioBuilder.ForText(string.Empty)
            .WithToolCallDelta("call_abc", "get_weather", """{"city":"SF"}""")
            .WithCompletedToolCall("call_abc", "get_weather", """{"city":"SF"}""")
            .Build();
        await using var app = await CreateAppAsync(provider, observationRuntime: observation);
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
        choice.GetProperty("message").GetProperty("tool_calls")[0].GetProperty("id").GetString().Should().Be("call_abc");
        choice.GetProperty("message").GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString()
            .Should()
            .Be("get_weather");

        provider.LastRequest.Should().BeNull();
        var command = app.Services.GetRequiredService<ChatCompletionsRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ToolSelection.ForwardedTools.Select(static tool => tool.ToolName)
            .Should()
            .Contain("get_weather");
        command.ToolSelection.AdditiveToolNames.Should().Contain("aevatar_invoke_team");
        command.ToolSelection.ForwardedTools.Single(static tool => tool.ToolName == "get_weather")
            .ParametersJson.Should().Contain("\"city\"");
    }

    [Fact]
    public async Task PostChatCompletions_WithModelSlug_ShouldResolveNyxRoutePreference()
    {
        var provider = new ChatCompletionsRecordingLLMProvider();
        var routeResolver = new ChatCompletionsRecordingRouteResolver(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["chrono-llm"] = "/api/v1/proxy/s/chrono-llm",
        });
        await using var app = await CreateAppAsync(
            provider,
            routeResolver: routeResolver,
            observationRuntime: ChatCompletionsObservationScenarioBuilder.ForText("routed").Build());
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
        provider.LastRequest.Should().BeNull();
        var command = app.Services.GetRequiredService<ChatCompletionsRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.Model.Should().Be("gpt-5-chat");
        command.RoutePreference.Should().Be("/api/v1/proxy/s/chrono-llm");
    }

    [Fact]
    public async Task PostChatCompletions_WhenRoutePinsTeamTool_ShouldDispatchTeamToolSelection()
    {
        var provider = new ChatCompletionsRecordingLLMProvider();
        var queryPort = ChatCompletionsStaticChatRoutePolicyQueryPort.ForSnapshot(
            new ChatRoutePolicySnapshot(
                TeamToolHintAction("team-1", "chat"),
                []));
        await using var app = await CreateAppAsync(
            provider,
            chatRoutePolicyQueryPort: queryPort,
            observationRuntime: ChatCompletionsObservationScenarioBuilder.ForText(string.Empty)
                .WithToolCallDelta("call_team_1", "aevatar_invoke_team", """{"team_id":"team-1"}""")
                .WithCompletedToolCall("call_team_1", "aevatar_invoke_team", """{"team_id":"team-1"}""")
                .Build());
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
        provider.LastRequest.Should().BeNull();
        var command = app.Services.GetRequiredService<ChatCompletionsRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ToolSelection.AdditiveToolNames.Should().Contain("aevatar_invoke_team");
        using var doc = JsonDocument.Parse(body);
        var choice = doc.RootElement.GetProperty("choices")[0];
        choice.GetProperty("finish_reason").GetString().Should().Be("stop");
        if (choice.GetProperty("message").TryGetProperty("tool_calls", out var toolCalls))
            toolCalls.ValueKind.Should().Be(JsonValueKind.Null);
        body.Should().NotContain("call_team_1");
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
        var observation = ChatCompletionsObservationScenarioBuilder.ForText("ignored").Build();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration["Aevatar:Authentication:Authority"] = "https://invalid.example";
        builder.Configuration["Aevatar:Authentication:Audience"] = "aevatar-api";
        builder.AddAevatarAuthentication();

        builder.Services.AddSingleton<ILLMProviderFactory>(provider);
        builder.Services.AddSingleton(observation);
        builder.Services.AddSingleton<ChatCompletionsRecordingActorDispatchPort>();
        builder.Services.AddSingleton<IActorDispatchPort>(static sp => sp.GetRequiredService<ChatCompletionsRecordingActorDispatchPort>());
        builder.Services.AddSingleton<IChatCompletionsCommandFacade, ChatCompletionsCommandFacade>();
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton<ILlmSessionRegistrationPort>(sessions);
        builder.Services.AddSingleton<ILlmSessionObservationScopeLeasePreparationPort>(static sp => sp.GetRequiredService<ChatCompletionsObservationRuntime>().ScopePreparationPort);
        builder.Services.AddSingleton<ILlmSessionObservationProjectionPort>(static sp => sp.GetRequiredService<ChatCompletionsObservationRuntime>().ProjectionPort);
        builder.Services.AddSingleton<ILlmSessionRunObservationService, LlmSessionRunObservationService>();
        builder.Services.AddSingleton<IResponsesCallerScopeResolver>(new ChatCompletionsStubCallerScopeResolver());
        builder.Services.AddSingleton<IResponsesChatRouteDecisionPort, ResponsesChatRouteDecisionPort>();
        builder.Services.AddSingleton<IChatRoutePolicyQueryPort>(ChatCompletionsStaticChatRoutePolicyQueryPort.ForSnapshot(
            new ChatRoutePolicySnapshot(ForwardToModelAction(string.Empty), [])));
        builder.Services.AddSingleton(new ChatRouteResolver(
            new ChatCompletionsStaticChatRouteFallbackProvider(string.Empty),
            DefaultToolSetRoutingOptions()));
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
        var provider = new ChatCompletionsRecordingLLMProvider();
        var toolProvider = new ChatCompletionsRecordingResponsesToolProvider(
            substituteTools: [new ChatCompletionsStubAgentTool("WebSearch", "would substitute client WebSearch")],
            additiveTools:
            [
                new ChatCompletionsStubAgentTool("use_skill", "load a skill"),
                new ChatCompletionsStubAgentTool("ornn_search_skills", "search Ornn skills"),
                new ChatCompletionsStubAgentTool("ornn_publish_skill", "publish Ornn skills"),
            ]);
        await using var app = await CreateAppAsync(
            provider,
            responsesToolProvider: toolProvider,
            observationRuntime: ChatCompletionsObservationScenarioBuilder.ForText("Hi").WithUsage(1, 1, 2).Build());
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
        provider.LastRequest.Should().BeNull();
        var command = app.Services.GetRequiredService<ChatCompletionsRecordingActorDispatchPort>()
            .Calls.Should().ContainSingle().Subject.Envelope.Payload.Unpack<LlmRunRequested>();
        command.ToolSelection.SubstitutedToolNames.Should().Contain("WebSearch");
        command.ToolSelection.AdditiveToolNames.Should().Contain(["use_skill", "ornn_search_skills", "ornn_publish_skill"]);
        command.ToolSelection.OwnedToolNames.Should().Contain(["WebSearch", "use_skill", "ornn_search_skills", "ornn_publish_skill"]);
    }

    [Fact]
    public void ChatCompletionsEndpointSource_ShouldNotReintroduceDirectLlmLoop()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src",
            "Aevatar.Mainnet.Host.Api",
            "ChatCompletions",
            "ChatCompletionsEndpoints.cs"));

        source.Should().NotContain("ILLMProviderFactory");
        source.Should().NotContain("IResponsesCompletionApplicationService");
        source.Should().NotContain("ChatStreamAsync");
        source.Should().NotContain("CollectAsync");
        source.Should().NotContain("completion.Accepted");
    }

    private static async Task<WebApplication> CreateAppAsync(
        ChatCompletionsRecordingLLMProvider provider,
        ChatCompletionsRecordingSessionStore? sessions = null,
        IResponsesCallerScopeResolver? callerScopeResolver = null,
        IChatRoutePolicyQueryPort? chatRoutePolicyQueryPort = null,
        IResponsesRouteResolver? routeResolver = null,
        IResponsesToolProvider? responsesToolProvider = null,
        ChatCompletionsObservationRuntime? observationRuntime = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ILLMProviderFactory>(provider);
        observationRuntime ??= ChatCompletionsObservationScenarioBuilder.ForText("ok").Build();
        builder.Services.AddSingleton(observationRuntime);
        builder.Services.AddSingleton<ChatCompletionsRecordingActorDispatchPort>();
        builder.Services.AddSingleton<IActorDispatchPort>(static sp => sp.GetRequiredService<ChatCompletionsRecordingActorDispatchPort>());
        builder.Services.AddSingleton<IChatCompletionsCommandFacade, ChatCompletionsCommandFacade>();
        sessions ??= new ChatCompletionsRecordingSessionStore();
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton<ILlmSessionRegistrationPort>(sessions);
        builder.Services.AddSingleton<ILlmSessionObservationScopeLeasePreparationPort>(static sp => sp.GetRequiredService<ChatCompletionsObservationRuntime>().ScopePreparationPort);
        builder.Services.AddSingleton<ILlmSessionObservationProjectionPort>(static sp => sp.GetRequiredService<ChatCompletionsObservationRuntime>().ProjectionPort);
        builder.Services.AddSingleton<ILlmSessionRunObservationService, LlmSessionRunObservationService>();
        builder.Services.AddSingleton(callerScopeResolver ?? new ChatCompletionsStubCallerScopeResolver());
        builder.Services.AddSingleton<IResponsesChatRouteDecisionPort, ResponsesChatRouteDecisionPort>();
        builder.Services.AddSingleton(chatRoutePolicyQueryPort ?? ChatCompletionsStaticChatRoutePolicyQueryPort.ForSnapshot(
            new ChatRoutePolicySnapshot(ForwardToModelAction(string.Empty), [])));
        builder.Services.AddSingleton(new ChatRouteResolver(
            new ChatCompletionsStaticChatRouteFallbackProvider(string.Empty),
            DefaultToolSetRoutingOptions()));
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

    private static IEnumerable<JsonDocument> ParseDataFrames(string body)
    {
        foreach (var frame in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "data: ";
            if (!frame.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var payload = frame[prefix.Length..];
            if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
                continue;
            yield return JsonDocument.Parse(payload);
        }
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate repository file.", Path.Combine(segments));
    }

    private static Microsoft.Extensions.Options.IOptions<ChatRoutingOptions> DefaultToolSetRoutingOptions() =>
        Microsoft.Extensions.Options.Options.Create(new ChatRoutingOptions
        {
            Defaults = new ChatRoutingDefaultsOptions
            {
                DefaultForwardToModelToolSetName = ToolSetNames.WorkspaceDefault,
            },
        });

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

    private sealed class ChatCompletionsRecordingActorDispatchPort : IActorDispatchPort
    {
        private readonly ChatCompletionsObservationRuntime _observationRuntime;

        public ChatCompletionsRecordingActorDispatchPort(ChatCompletionsObservationRuntime observationRuntime)
        {
            _observationRuntime = observationRuntime;
        }

        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope));
            var command = envelope.Payload.Unpack<LlmRunRequested>();
            _observationRuntime.PublishAll(command);
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class ChatCompletionsStubCallerScopeResolver : IResponsesCallerScopeResolver
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

    private sealed class ChatCompletionsObservationRuntime
    {
        private readonly IReadOnlyList<EventEnvelope> _events;

        public ChatCompletionsObservationRuntime(IReadOnlyList<EventEnvelope> events)
        {
            _events = events;
            ScopePreparationPort = new ChatCompletionsObservationScopeLeasePreparationPort();
            ProjectionPort = new ChatCompletionsObservationProjectionPort();
        }

        public ChatCompletionsObservationScopeLeasePreparationPort ScopePreparationPort { get; }

        public ChatCompletionsObservationProjectionPort ProjectionPort { get; }

        public void PublishAll(LlmRunRequested command)
        {
            foreach (var envelope in _events)
            {
                ProjectionPort.Sink?.Push(RewriteRunEvent(envelope, command));
            }
        }

        private static EventEnvelope RewriteRunEvent(EventEnvelope envelope, LlmRunRequested command)
        {
            if (!envelope.Payload.Is(LlmRunCompleted.Descriptor))
                return envelope;

            var completed = envelope.Payload.Unpack<LlmRunCompleted>();
            var forwardedToolNames = command.ToolSelection?.ForwardedTools
                .Select(static tool => tool.ToolName)
                .Except(command.ToolSelection.SubstitutedToolNames, StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal) ?? [];
            for (var i = completed.ForwardedToolCalls.Count - 1; i >= 0; i--)
            {
                if (!forwardedToolNames.Contains(completed.ForwardedToolCalls[i].ToolName))
                    completed.ForwardedToolCalls.RemoveAt(i);
            }

            return new EventEnvelope
            {
                Id = envelope.Id,
                Payload = Any.Pack(completed),
            };
        }
    }

    private sealed class ChatCompletionsObservationScenarioBuilder
    {
        private enum ObservationTerminalState
        {
            Completed,
            Failed,
            Cancelled,
            None,
        }

        private readonly List<EventEnvelope> _events = [];
        private readonly List<LlmSessionRuntimeToolCall> _completedToolCalls = [];
        private readonly string _text;
        private TokenUsage? _usage;
        private ObservationTerminalState _terminalState = ObservationTerminalState.Completed;
        private string? _failureMessage;

        private ChatCompletionsObservationScenarioBuilder(string text)
        {
            _text = text;
        }

        public static ChatCompletionsObservationScenarioBuilder ForText(string text) => new(text);

        public ChatCompletionsObservationScenarioBuilder WithChunkText(string deltaText)
        {
            _events.Add(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new LlmStreamChunkObserved
                {
                    DeltaText = deltaText,
                    ResponseId = "placeholder",
                    RunId = "placeholder:llm-run",
                }),
            });
            return this;
        }

        public ChatCompletionsObservationScenarioBuilder WithToolCallDelta(string callId, string toolName, string argumentsJson)
        {
            _events.Add(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Payload = Any.Pack(new LlmStreamChunkObserved
                {
                    ResponseId = "placeholder",
                    RunId = "placeholder:llm-run",
                    ToolCallDelta = new LlmSessionRuntimeToolCall
                    {
                        CallId = callId,
                        ToolName = toolName,
                        ArgumentsJson = argumentsJson,
                    },
                }),
            });
            return this;
        }

        public ChatCompletionsObservationScenarioBuilder WithCompletedToolCall(string callId, string toolName, string resultJson)
        {
            _completedToolCalls.Add(new LlmSessionRuntimeToolCall
            {
                CallId = callId,
                ToolName = toolName,
                ArgumentsJson = resultJson,
            });
            return this;
        }

        public ChatCompletionsObservationScenarioBuilder WithFailed(string failureMessage)
        {
            _terminalState = ObservationTerminalState.Failed;
            _failureMessage = failureMessage;
            return this;
        }

        public ChatCompletionsObservationScenarioBuilder WithCancelled()
        {
            _terminalState = ObservationTerminalState.Cancelled;
            _failureMessage = null;
            return this;
        }

        public ChatCompletionsObservationScenarioBuilder WithoutTerminal()
        {
            _terminalState = ObservationTerminalState.None;
            _failureMessage = null;
            return this;
        }

        public ChatCompletionsObservationScenarioBuilder WithUsage(int promptTokens, int completionTokens, int totalTokens)
        {
            _usage = new TokenUsage(promptTokens, completionTokens, totalTokens);
            return this;
        }

        public ChatCompletionsObservationRuntime Build()
        {
            switch (_terminalState)
            {
                case ObservationTerminalState.Completed:
                    var completed = new LlmRunCompleted
                    {
                        ResponseId = "placeholder",
                        RunId = "placeholder:llm-run",
                        OutputText = _text,
                        CompletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    };
                    completed.ForwardedToolCalls.AddRange(_completedToolCalls.Select(static call => call.Clone()));
                    if (_usage is not null)
                    {
                        completed.Usage = new LlmSessionTokenUsage
                        {
                            PromptTokens = _usage.PromptTokens,
                            CompletionTokens = _usage.CompletionTokens,
                            TotalTokens = _usage.TotalTokens,
                        };
                    }

                    _events.Add(new EventEnvelope
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Payload = Any.Pack(completed),
                    });
                    break;
                case ObservationTerminalState.Failed:
                    _events.Add(new EventEnvelope
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Payload = Any.Pack(new LlmRunFailed
                        {
                            ResponseId = "placeholder",
                            RunId = "placeholder:llm-run",
                            FailureMessage = _failureMessage ?? "LLM run failed.",
                            FailedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                        }),
                    });
                    break;
                case ObservationTerminalState.Cancelled:
                    _events.Add(new EventEnvelope
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Payload = Any.Pack(new LlmRunCancelled
                        {
                            ResponseId = "placeholder",
                            RunId = "placeholder:llm-run",
                            CancelledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                        }),
                    });
                    break;
                case ObservationTerminalState.None:
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported terminal state '{_terminalState}'.");
            }
            return new ChatCompletionsObservationRuntime(_events.ToArray());
        }
    }

    private sealed class ChatCompletionsObservationScopeLeasePreparationPort
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

    private sealed class ChatCompletionsObservationProjectionPort : ILlmSessionObservationProjectionPort
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
                    new ChatCompletionsObservationLease(actorId, responseId),
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

    private sealed record ChatCompletionsObservationLease(string ActorId, string ResponseId)
        : ILlmSessionObservationProjectionLease;

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
