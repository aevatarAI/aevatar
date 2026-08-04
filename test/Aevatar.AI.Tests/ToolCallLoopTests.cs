using System.Runtime.CompilerServices;
using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Hooks;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class ToolCallLoopTests
{
    [Fact]
    public async Task ExecuteAsync_WhenNoToolCalls_ShouldReturnAssistantContent()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse { Content = "final-answer" },
        ]);
        var loop = NewToolCallLoop(new ToolManager());
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 2, CancellationToken.None);

        result.Should().Be("final-answer");
        messages.Should().ContainSingle(m => m.Role == "assistant" && m.Content == "final-answer");
        provider.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenToolCallThenFollowUp_ShouldExecuteToolAndReturnFinalContent()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tc-1",
                        Name = "echo",
                        ArgumentsJson = """{"q":"abc"}""",
                    },
                ],
            },
            new LLMResponse { Content = "done" },
        ]);
        var tools = new ToolManager();
        var exactTool = new DelegateTool("echo", args => $"RESULT:{args}");
        tools.Register(exactTool);
        var loop = NewToolCallLoop(tools);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = [exactTool] };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 3, CancellationToken.None);

        result.Should().Be("done");
        messages.Any(m => m.Role == "assistant" && m.ToolCalls?.Count == 1).Should().BeTrue();
        messages.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "tc-1" && m.Content == """RESULT:{"q":"abc"}""");
        messages.Should().Contain(m => m.Role == "assistant" && m.Content == "done");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldExecuteExactRequestToolInsteadOfActorToolWithSameName()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls = [new ToolCall { Id = "tc-exact", Name = "echo", ArgumentsJson = "{}" }],
            },
            new LLMResponse { Content = "done" },
        ]);
        var actorTool = new DelegateTool("echo", _ => "actor");
        var requestTool = new DelegateTool("echo", _ => "request");
        var actorTools = new ToolManager();
        actorTools.Register(actorTool);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };

        await NewToolCallLoop(actorTools).ExecuteAsync(
            provider,
            messages,
            new LLMRequest { Messages = [], Tools = [requestTool] },
            maxRounds: 3,
            CancellationToken.None);

        messages.Should().Contain(message => message.Role == "tool" && message.Content == "request");
        messages.Should().NotContain(message => message.Role == "tool" && message.Content == "actor");
    }

    [Fact]
    public async Task ExecuteAsync_WithNoRequestTools_ShouldNotFallBackToActorTool()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls = [new ToolCall { Id = "tc-forged", Name = "echo", ArgumentsJson = "{}" }],
            },
            new LLMResponse { Content = "done" },
        ]);
        var actorTools = new ToolManager();
        actorTools.Register(new DelegateTool("echo", _ => "actor"));
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };

        await NewToolCallLoop(actorTools).ExecuteAsync(
            provider,
            messages,
            new LLMRequest { Messages = [], Tools = null },
            maxRounds: 3,
            CancellationToken.None);

        messages.Should().Contain(message =>
            IsSafeRejectedToolFailure(message, "echo"));
        messages.Should().NotContain(message => message.Role == "tool" && message.Content == "actor");
    }

    [Fact]
    public async Task ExecuteAsync_WhenLlmMiddlewareRemovesTool_ShouldNotExecuteRemovedTool()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls = [new ToolCall { Id = "tc-removed", Name = "echo", ArgumentsJson = "{}" }],
            },
            new LLMResponse { Content = "done" },
        ]);
        var executions = 0;
        var exactTool = new DelegateTool("echo", _ =>
        {
            executions++;
            return "executed";
        });
        var tools = new ToolManager();
        tools.Register(exactTool);
        var middleware = new DelegateLlmCallMiddleware(async (context, next) =>
        {
            context.Request = CopyRequestWithTools(context.Request, null);
            await next();
        });
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };

        await NewToolCallLoop(tools, llmMiddlewares: [middleware]).ExecuteAsync(
            provider,
            messages,
            new LLMRequest { Messages = [], Tools = [exactTool] },
            maxRounds: 3,
            CancellationToken.None);

        provider.Requests[0].Tools.Should().BeNull();
        executions.Should().Be(0);
        messages.Should().Contain(message =>
            IsSafeRejectedToolFailure(message, "echo"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenLlmMiddlewareAddsTool_ShouldHideAndRejectAddedTool()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls = [new ToolCall { Id = "tc-added", Name = "added", ArgumentsJson = "{}" }],
            },
            new LLMResponse { Content = "done" },
        ]);
        var exactTool = new DelegateTool("echo", _ => "exact");
        var addedExecutions = 0;
        var addedTool = new DelegateTool("added", _ =>
        {
            addedExecutions++;
            return "added";
        });
        var tools = new ToolManager();
        tools.Register(exactTool);
        var middleware = new DelegateLlmCallMiddleware(async (context, next) =>
        {
            context.Request = CopyRequestWithTools(context.Request, [exactTool, addedTool]);
            await next();
        });
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };

        await NewToolCallLoop(tools, llmMiddlewares: [middleware]).ExecuteAsync(
            provider,
            messages,
            new LLMRequest { Messages = [], Tools = [exactTool] },
            maxRounds: 3,
            CancellationToken.None);

        provider.Requests[0].Tools.Should().ContainSingle().Which.Should().BeSameAs(exactTool);
        addedExecutions.Should().Be(0);
        messages.Should().Contain(message =>
            IsSafeRejectedToolFailure(message, "added"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenLlmMiddlewareReplacesToolWithSameName_ShouldRejectBothObjects()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls = [new ToolCall { Id = "tc-replaced", Name = "echo", ArgumentsJson = "{}" }],
            },
            new LLMResponse { Content = "done" },
        ]);
        var exactExecutions = 0;
        var replacementExecutions = 0;
        var exactTool = new DelegateTool("echo", _ =>
        {
            exactExecutions++;
            return "exact";
        });
        var replacementTool = new DelegateTool("echo", _ =>
        {
            replacementExecutions++;
            return "replacement";
        });
        var tools = new ToolManager();
        tools.Register(exactTool);
        var middleware = new DelegateLlmCallMiddleware(async (context, next) =>
        {
            context.Request = CopyRequestWithTools(context.Request, [replacementTool]);
            await next();
        });
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };

        await NewToolCallLoop(tools, llmMiddlewares: [middleware]).ExecuteAsync(
            provider,
            messages,
            new LLMRequest { Messages = [], Tools = [exactTool] },
            maxRounds: 3,
            CancellationToken.None);

        provider.Requests[0].Tools.Should().BeNull();
        exactExecutions.Should().Be(0);
        replacementExecutions.Should().Be(0);
        messages.Should().Contain(message =>
            IsSafeRejectedToolFailure(message, "echo"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenLlmMiddlewareMutatesToolsAfterProvider_ShouldUseProviderSnapshot()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls = [new ToolCall { Id = "tc-after", Name = "echo", ArgumentsJson = "{}" }],
            },
            new LLMResponse { Content = "done" },
        ]);
        var exactExecutions = 0;
        var replacementExecutions = 0;
        var exactTool = new DelegateTool("echo", _ =>
        {
            exactExecutions++;
            return "exact";
        });
        var replacementTool = new DelegateTool("echo", _ =>
        {
            replacementExecutions++;
            return "replacement";
        });
        var tools = new ToolManager();
        tools.Register(exactTool);
        var middleware = new DelegateLlmCallMiddleware(async (context, next) =>
        {
            await next();
            ((IList<IAgentTool>)context.Request.Tools!)[0] = replacementTool;
        });
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };

        await NewToolCallLoop(tools, llmMiddlewares: [middleware]).ExecuteAsync(
            provider,
            messages,
            new LLMRequest { Messages = [], Tools = [exactTool] },
            maxRounds: 3,
            CancellationToken.None);

        exactExecutions.Should().Be(1);
        replacementExecutions.Should().Be(0);
        messages.Should().Contain(message => message.Role == "tool" && message.Content == "exact");
    }

    [Fact]
    public async Task ExecuteAsync_WhenBaseRequestIdPresent_ShouldKeepStableRequestIdAndEmitPerCallTypedContext()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tc-identity",
                        Name = "echo",
                        ArgumentsJson = "{}",
                    },
                ],
            },
            new LLMResponse { Content = "done" },
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("echo", _ => "{}"));
        var loop = NewToolCallLoop(tools);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest
        {
            Messages = [],
            Tools = tools.GetAll(),
            RequestId = "session-99",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflow.run_id"] = "run-99",
            },
        };

        await loop.ExecuteAsync(provider, messages, request, maxRounds: 3, CancellationToken.None);

        provider.Requests.Should().HaveCount(2);
        provider.Requests[0].RequestId.Should().Be("session-99");
        provider.Requests[1].RequestId.Should().Be("session-99");
        provider.Requests.Should().OnlyContain(x => x.Metadata != null && x.Metadata["workflow.run_id"] == "run-99");
        provider.Requests.Should().OnlyContain(x => !x.Metadata!.ContainsKey(LLMRequestMetadataKeys.CallId));
        provider.Requests[0].ToolContext!.Request.CallId.Should().Be("session-99");
        provider.Requests[1].ToolContext!.Request.CallId.Should().Be("session-99:tool-round:2");
    }

    [Fact]
    public void AgentToolExecutionContextMapper_ShouldIgnoreOwnedKeysAndKeepExternalMetadataOnly()
    {
        var context = AgentToolExecutionContextMapper.FromMetadata(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.RequestId] = "request-a",
            [LLMRequestMetadataKeys.CallId] = "call-a",
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "access-token",
            [LLMRequestMetadataKeys.NyxIdOrgToken] = "org-token",
            [LLMRequestMetadataKeys.SenderNyxIdAccessToken] = "sender-token",
            [LLMRequestMetadataKeys.ModelOverride] = "model-a",
            [LLMRequestMetadataKeys.NyxIdRoutePreference] = "/preferred",
            [LLMRequestMetadataKeys.MaxToolRoundsOverride] = "7",
            [LLMRequestMetadataKeys.UserMemoryPrompt] = "memory-a",
            [LLMRequestMetadataKeys.ConnectedServicesContext] = "{\"services\":[]}",
            [LLMRequestMetadataKeys.OwnerSubject] = "owner-a",
            [LLMRequestMetadataKeys.ResponseId] = "response-a",
            [LLMRequestMetadataKeys.SenderBindingId] = "binding-a",
            ["scope_id"] = "scope-a",
            ["channel.platform"] = "lark",
            ["channel.sender_id"] = "ou_1",
            ["trace-id"] = "trace-1",
        });

        context.Request.RequestId.Should().BeNull();
        context.Request.CallId.Should().BeNull();
        context.Credentials.NyxIdAccessToken.Should().BeNull();
        context.Credentials.NyxIdOrgToken.Should().BeNull();
        context.Credentials.SenderNyxIdAccessToken.Should().BeNull();
        context.Caller.ScopeId.Should().BeNull();
        context.Caller.OwnerSubject.Should().BeNull();
        context.Caller.ResponseId.Should().BeNull();
        context.Channel.Platform.Should().BeNull();
        context.Channel.SenderId.Should().BeNull();
        context.SenderBinding.BindingId.Should().BeNull();
        context.Routing.ModelOverride.Should().BeNull();
        context.Routing.NyxIdRoutePreference.Should().BeNull();
        context.Routing.MaxToolRoundsOverride.Should().BeNull();
        context.Routing.UserMemoryPrompt.Should().BeNull();
        context.ConnectedServices.ContextJson.Should().BeNull();
        context.ExternalMetadata.Should().ContainSingle();
        context.ExternalMetadata["trace-id"].Should().Be("trace-1");
    }

    [Fact]
    public async Task ExecuteAsync_WhenMetadataHasOwnedKeys_ShouldKeepOnlyExternalAnnotationsForToolExecution()
    {
        string? capturedToken = null;
        string? capturedScope = null;
        string? capturedExternal = null;
        string? capturedCallId = null;
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tool-call-1",
                        Name = "capture",
                        ArgumentsJson = "{}",
                    },
                ],
            },
            new LLMResponse { Content = "done" },
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("capture", _ =>
        {
            capturedToken = AgentToolRequestContext.NyxIdAccessToken;
            capturedScope = AgentToolRequestContext.ScopeId;
            capturedExternal = AgentToolRequestContext.TryGetExternalMetadata("trace-id");
            capturedCallId = AgentToolRequestContext.CallId;
            return "{}";
        }));
        var loop = NewToolCallLoop(tools);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest
        {
            Messages = [],
            Tools = tools.GetAll(),
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "metadata-token",
                [LLMRequestMetadataKeys.ScopeId] = "metadata-scope",
                [LLMRequestMetadataKeys.CallId] = "metadata-call",
                ["trace-id"] = "trace-1",
            },
        };

        await loop.ExecuteAsync(provider, messages, request, maxRounds: 2, CancellationToken.None);

        capturedToken.Should().BeNull();
        capturedScope.Should().BeNull();
        capturedExternal.Should().Be("trace-1");
        capturedCallId.Should().Be("tool-call-1");
        messages.Should().ContainSingle(m => m.Role == "tool" && m.ToolCallId == "tool-call-1");
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteToolCallsAsync_WhenMetadataHasOwnedKeys_ShouldPushOnlyExternalAnnotations()
    {
        string? capturedToken = null;
        string? capturedScope = null;
        string? capturedExternal = null;
        string? capturedCallId = null;
        var tools = new ToolManager();
        tools.Register(new DelegateTool("capture", _ =>
        {
            capturedToken = AgentToolRequestContext.NyxIdAccessToken;
            capturedScope = AgentToolRequestContext.ScopeId;
            capturedExternal = AgentToolRequestContext.TryGetExternalMetadata("trace-id");
            capturedCallId = AgentToolRequestContext.CallId;
            return """{"ok":true}""";
        }));
        var loop = NewToolCallLoop(tools);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "metadata-token",
            [LLMRequestMetadataKeys.ScopeId] = "metadata-scope",
            [LLMRequestMetadataKeys.CallId] = "metadata-call",
            ["trace-id"] = "trace-standalone",
        };
        var method = typeof(ToolCallLoop).GetMethod(
            "ExecuteToolCallsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var task = (Task)method!.Invoke(loop,
        [
            new List<ToolCall>
            {
                new()
                {
                    Id = "standalone-tool-call",
                    Name = "capture",
                    ArgumentsJson = "{}",
                },
            },
            messages,
            metadata,
            CancellationToken.None,
        ])!;

        await task;

        capturedToken.Should().BeNull();
        capturedScope.Should().BeNull();
        capturedExternal.Should().Be("trace-standalone");
        capturedCallId.Should().Be("standalone-tool-call");
        messages.Should().ContainSingle(m =>
            m.Role == "tool" &&
            m.ToolCallId == "standalone-tool-call" &&
            m.Content == """{"ok":true}""");
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTypedToolContextExists_ShouldExposeRequestExternalMetadataToToolExecution()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tc-context",
                        Name = "capture_context",
                        ArgumentsJson = "{}",
                    },
                ],
            },
            new LLMResponse { Content = "done" },
        ]);
        var tools = new ToolManager();
        string? observedOperatorUserId = null;
        string? observedExplicitMetadata = null;
        string? observedAccessToken = null;
        tools.Register(new DelegateTool("capture_context", _ =>
        {
            observedOperatorUserId = AgentToolRequestContext.TryGetExternalMetadata("channel.lark.operator_user_id");
            observedExplicitMetadata = AgentToolRequestContext.TryGetExternalMetadata("explicit");
            observedAccessToken = AgentToolRequestContext.NyxIdAccessToken;
            return "{}";
        }));
        var loop = NewToolCallLoop(tools);
        var messages = new List<ChatMessage> { ChatMessage.User("approve it") };
        var request = new LLMRequest
        {
            Messages = [],
            Tools = tools.GetAll(),
            RequestId = "session-operator",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["channel.lark.operator_user_id"] = "lark-user-1",
                ["explicit"] = "from-request",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "metadata-token",
            },
            ToolContext = AgentToolExecutionContext.Empty with
            {
                Credentials = new AgentToolCredentials("typed-token", null, null),
                ExternalMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["explicit"] = "from-tool-context",
                },
            },
        };

        await loop.ExecuteAsync(provider, messages, request, maxRounds: 2, CancellationToken.None);

        observedOperatorUserId.Should().Be("lark-user-1");
        observedExplicitMetadata.Should().Be("from-tool-context");
        observedAccessToken.Should().Be("typed-token");
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenBaseRequestIdPresent_ShouldExposeStableRequestIdAndPerCallIdToLlmMiddlewareMetadata()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tc-identity",
                        Name = "echo",
                        ArgumentsJson = "{}",
                    },
                ],
            },
            new LLMResponse { Content = "done" },
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("echo", _ => "{}"));
        var requestIdMiddleware = new CaptureLlmRequestIdentityMiddleware();
        var loop = NewToolCallLoop(
            tools,
            hooks: null,
            llmMiddlewares: [requestIdMiddleware]);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest
        {
            Messages = [],
            Tools = tools.GetAll(),
            RequestId = "session-105",
        };

        await loop.ExecuteAsync(provider, messages, request, maxRounds: 3, CancellationToken.None);

        requestIdMiddleware.RequestIds.Should().Equal("session-105", "session-105");
        requestIdMiddleware.CallIds.Should().Equal("session-105", "session-105:tool-round:2");
    }

    [Fact]
    public async Task ExecuteAsync_WhenLlmMiddlewareRuns_ShouldExposeStreamingContextAndAggregateProviderStream()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                Content = "stream-answer",
                ReasoningContent = "stream-reasoning",
                FinishReason = "stop",
            },
        ]);
        bool? observedIsStreaming = null;
        var middleware = new DelegateLlmCallMiddleware(async (context, next) =>
        {
            observedIsStreaming = context.IsStreaming;
            await next();
            context.Response.Should().NotBeNull();
            context.Response!.Content.Should().Be("stream-answer");
            context.Response.ReasoningContent.Should().Be("stream-reasoning");
        });
        var loop = NewToolCallLoop(
            new ToolManager(),
            hooks: null,
            llmMiddlewares: [middleware]);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 1, CancellationToken.None);

        result.Should().Be("stream-answer");
        observedIsStreaming.Should().BeTrue();
        provider.Requests.Should().HaveCount(1);
        messages.Should().ContainSingle(m =>
            m.Role == "assistant" &&
            m.Content == "stream-answer" &&
            m.ReasoningContent == "stream-reasoning");
    }

    [Fact]
    public async Task ExecuteAsync_WhenHookMutatesPreparedToolCall_ShouldRejectRewrite()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tc-2",
                        Name = "original",
                        ArgumentsJson = """{"x":1}""",
                    },
                ],
            },
            new LLMResponse { Content = "ok" },
        ]);

        var capturedArguments = string.Empty;
        var tools = new ToolManager();
        tools.Register(new DelegateTool("mutated", args =>
        {
            capturedArguments = args;
            return "mutated-result";
        }));

        var hook = new RecordingHook
        {
            OnToolStart = ctx =>
            {
                ctx.ToolName = "mutated";
                ctx.ToolArguments = """{"x":999}""";
            },
        };
        var hooks = new AgentHookPipeline([hook]);
        var loop = NewToolCallLoop(tools, hooks);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = tools.GetAll() };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 2, CancellationToken.None);

        result.Should().Be("ok");
        capturedArguments.Should().BeEmpty();
        hook.ToolStartCount.Should().Be(1);
        hook.ToolEndCount.Should().Be(0, "a rejected rewrite never executes the prepared operation");
        hook.ToolResultAtEnd.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenMaxRoundsReachedWithoutTerminalContent_ShouldReturnNull()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tc-3",
                        Name = "echo",
                        ArgumentsJson = "{}",
                    },
                ],
            },
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("echo", _ => "{}"));
        var hook = new RecordingHook();
        var llmMiddlewareCalls = 0;
        var llmMiddleware = new DelegateLlmCallMiddleware(async (_, next) =>
        {
            llmMiddlewareCalls++;
            await next();
        });
        var loop = NewToolCallLoop(
            tools,
            hooks: new AgentHookPipeline([hook]),
            llmMiddlewares: [llmMiddleware]);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = tools.GetAll() };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 1, CancellationToken.None);

        result.Should().BeNull();
        messages.Count(m => m.Role == "assistant" && m.ToolCalls?.Count == 1).Should().Be(1);
        messages.Should().ContainSingle(m => m.Role == "tool");
        // Final call should have been made without tools
        provider.Requests.Should().HaveCount(2);
        provider.Requests[1].Tools.Should().BeNull();
        llmMiddlewareCalls.Should().Be(2);
        hook.LlmStartCount.Should().Be(2);
        hook.LlmEndCount.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldInvokeLLMHookLifecycle()
    {
        var provider = new QueueLLMProvider([new LLMResponse { Content = "ok" }]);
        var hook = new RecordingHook();
        var loop = NewToolCallLoop(new ToolManager(), new AgentHookPipeline([hook]));
        var messages = new List<ChatMessage> { ChatMessage.User("u") };
        var request = new LLMRequest { Messages = [], Tools = null };

        await loop.ExecuteAsync(provider, messages, request, maxRounds: 1, CancellationToken.None);

        hook.LlmStartCount.Should().Be(1);
        hook.LlmEndCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenLlmMiddlewareTerminates_ShouldReturnMiddlewareResponseWithoutCallingProvider()
    {
        var provider = new QueueLLMProvider([]);
        var middleware = new DelegateLlmCallMiddleware((context, _) =>
        {
            context.Terminate = true;
            context.Response = new LLMResponse { Content = "middleware-answer" };
            return Task.CompletedTask;
        });
        var hook = new RecordingHook();
        var loop = NewToolCallLoop(
            new ToolManager(),
            hooks: new AgentHookPipeline([hook]),
            llmMiddlewares: [middleware]);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 1, CancellationToken.None);

        result.Should().Be("middleware-answer");
        provider.Requests.Should().BeEmpty();
        messages.Should().ContainSingle(m => m.Role == "assistant" && m.Content == "middleware-answer");
        hook.LlmStartCount.Should().Be(1);
        hook.LlmEndCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenFinishReasonLength_ShouldInjectNudgeAndConcatenateContent()
    {
        // First response: truncated (finish_reason = "length", no tool calls)
        // Second response: normal completion after continuation nudge
        var provider = new QueueLLMProvider(
        [
            new LLMResponse { Content = "partial answer...", FinishReason = "length" },
            new LLMResponse { Content = "...continued and done" },
        ]);
        var loop = NewToolCallLoop(new ToolManager());
        var messages = new List<ChatMessage> { ChatMessage.User("do something complex") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 5, CancellationToken.None);

        // The returned result should be the full concatenated content, not just the tail.
        result.Should().Be("partial answer......continued and done");
        provider.Requests.Should().HaveCount(2, "should have retried after length truncation");
        // Individual partial messages are preserved in history for the LLM context
        messages.Should().Contain(m => m.Role == "assistant" && m.Content == "partial answer...");
        messages.Should().Contain(m => m.Role == "user" && m.Content!.Contains("cut off due to length"));
        // Final concatenated message in history
        messages.Last(m => m.Role == "assistant").Content.Should().Be("partial answer......continued and done");
    }

    [Fact]
    public async Task ExecuteAsync_WhenFinishReasonLength_ShouldRespectMaxRecoveries()
    {
        // All responses are truncated — should stop after MaxLengthRecoveries (3) attempts
        var responses = Enumerable.Range(0, 5)
            .Select(i => new LLMResponse { Content = $"part-{i}|", FinishReason = "length" })
            .ToList();
        // Add a final-call-without-tools response for when maxRounds is exhausted
        responses.Add(new LLMResponse { Content = "forced-final" });

        var provider = new QueueLLMProvider(responses);
        var loop = NewToolCallLoop(new ToolManager());
        var messages = new List<ChatMessage> { ChatMessage.User("never ending") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 10, CancellationToken.None);

        // 1 initial + 3 recoveries = 4 calls, then on the 4th truncation it exits
        provider.Requests.Should().HaveCount(4);
        // All 4 partial segments concatenated
        result.Should().Be("part-0|part-1|part-2|part-3|");
    }

    [Fact]
    public async Task ExecuteAsync_WhenFinishReasonMaxTokens_ShouldAlsoRecover()
    {
        // Some providers use "max_tokens" instead of "length"
        var provider = new QueueLLMProvider(
        [
            new LLMResponse { Content = "cut off", FinishReason = "max_tokens" },
            new LLMResponse { Content = " completed" },
        ]);
        var loop = NewToolCallLoop(new ToolManager());
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 5, CancellationToken.None);

        result.Should().Be("cut off completed");
        provider.Requests.Should().HaveCount(2);
    }

    [Fact]
    public void IsLengthTruncated_ShouldDetectKnownReasons_CaseInsensitive()
    {
        // Lowercase (direct string values)
        ToolCallLoop.IsLengthTruncated("length").Should().BeTrue();
        ToolCallLoop.IsLengthTruncated("max_tokens").Should().BeTrue();
        // PascalCase (from provider enum .ToString(), e.g. Tornado)
        ToolCallLoop.IsLengthTruncated("Length").Should().BeTrue();
        ToolCallLoop.IsLengthTruncated("Max_Tokens").Should().BeTrue();
        // Non-truncation reasons
        ToolCallLoop.IsLengthTruncated("stop").Should().BeFalse();
        ToolCallLoop.IsLengthTruncated("Stop").Should().BeFalse();
        ToolCallLoop.IsLengthTruncated(null).Should().BeFalse();
        ToolCallLoop.IsLengthTruncated("").Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoToolCalls_ShouldPropagateReasoningContent()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse { Content = "answer", ReasoningContent = "thinking-step" },
        ]);
        var loop = NewToolCallLoop(new ToolManager());
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 2, CancellationToken.None);

        result.Should().Be("answer");
        messages.Should().ContainSingle(m => m.Role == "assistant");
        var assistant = messages.Single(m => m.Role == "assistant");
        assistant.Content.Should().Be("answer");
        assistant.ReasoningContent.Should().Be("thinking-step");
    }

    [Fact]
    public async Task ExecuteAsync_WhenToolCallThenFollowUp_ShouldPropagateReasoningOnBothRounds()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                Content = "will use tool",
                ReasoningContent = "first-thought",
                ToolCalls =
                [
                    new ToolCall { Id = "tc-1", Name = "echo", ArgumentsJson = "{}" },
                ],
            },
            new LLMResponse { Content = "final", ReasoningContent = "second-thought" },
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("echo", _ => "ok"));
        var loop = NewToolCallLoop(tools);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = tools.GetAll() };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 3, CancellationToken.None);

        result.Should().Be("final");
        var toolCallAssistant = messages.Single(m => m.Role == "assistant" && m.ToolCalls is { Count: 1 });
        toolCallAssistant.Content.Should().Be("will use tool");
        toolCallAssistant.ReasoningContent.Should().Be("first-thought");
        provider.Requests.Should().HaveCount(2);
        provider.Requests[1].Messages.Should().Contain(m =>
            m.Role == "assistant" &&
            m.ToolCalls != null &&
            m.ToolCalls.Count == 1 &&
            m.Content == "will use tool" &&
            m.ReasoningContent == "first-thought");
        var finalAssistant = messages.Last(m => m.Role == "assistant");
        finalAssistant.ReasoningContent.Should().Be("second-thought");
    }

    [Fact]
    public async Task ExecuteAsync_WhenLengthRecovery_ShouldPropagateReasoningContent()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                Content = "partial",
                ReasoningContent = "thinking-partial",
                FinishReason = "length",
            },
            new LLMResponse
            {
                Content = " continued",
                ReasoningContent = "thinking-continued",
            },
        ]);
        var loop = NewToolCallLoop(new ToolManager());
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = null };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 3, CancellationToken.None);

        result.Should().Be("partial continued");
        var partialAssistant = messages.First(m => m.Role == "assistant");
        partialAssistant.ReasoningContent.Should().Be("thinking-partial");
    }

    [Fact]
    public async Task ExecuteAsync_WhenMaxRoundsExhausted_ShouldPropagateReasoningInFinalCall()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls = [new ToolCall { Id = "tc-1", Name = "echo", ArgumentsJson = "{}" }],
            },
            new LLMResponse { Content = "summary", ReasoningContent = "final-thought" },
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("echo", _ => "ok"));
        var loop = NewToolCallLoop(tools);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = tools.GetAll() };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 1, CancellationToken.None);

        result.Should().Be("summary");
        var lastAssistant = messages.Last(m => m.Role == "assistant");
        lastAssistant.ReasoningContent.Should().Be("final-thought");
    }

    [Fact]
    public async Task ExecuteAsync_WhenHookBlocksToolCalls_ShouldPropagateReasoningContent()
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                Content = "blocked-content",
                ReasoningContent = "blocked-thinking",
                ToolCalls = [new ToolCall { Id = "tc-1", Name = "echo", ArgumentsJson = "{}" }],
            },
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("echo", _ => "ok"));
        var hook = new BlockPostSamplingHook();
        var loop = NewToolCallLoop(tools, new AgentHookPipeline([hook]));
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = tools.GetAll() };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 2, CancellationToken.None);

        result.Should().Be("blocked-content");
        var assistant = messages.Single(m => m.Role == "assistant");
        assistant.Content.Should().Be("blocked-content");
        assistant.ReasoningContent.Should().Be("blocked-thinking");
    }

    [Fact]
    public async Task ExecuteAsync_WhenDsmlTextToolCalls_ShouldPropagateReasoningContent()
    {
        var dsmlContent = "I will search now.\n<function_calls><invoke name=\"echo\"><parameter name=\"q\">test</parameter></invoke></function_calls>";
        var provider = new QueueLLMProvider(
        [
            new LLMResponse { Content = dsmlContent, ReasoningContent = "dsml-thinking" },
            new LLMResponse { Content = "final-after-dsml", ReasoningContent = "final-thinking" },
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("echo", _ => "echo-result"));
        var loop = NewToolCallLoop(tools);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = tools.GetAll() };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 3, CancellationToken.None);

        result.Should().Be("final-after-dsml");
        var dsmlAssistant = messages.Single(m =>
            m.Role == "assistant" &&
            m.ToolCalls is { Count: 1 } &&
            m.ToolCalls[0].Name == "echo");
        dsmlAssistant.Content.Should().Be("I will search now.");
        dsmlAssistant.ReasoningContent.Should().Be("dsml-thinking");
        var forwardedDsmlAssistant = provider.Requests[1].Messages.Single(m =>
            m.Role == "assistant" &&
            m.ToolCalls is { Count: 1 } &&
            m.ToolCalls[0].Name == "echo");
        forwardedDsmlAssistant.ReasoningContent.Should().Be("dsml-thinking");
        var finalAssistant = messages.Last(m => m.Role == "assistant");
        finalAssistant.ReasoningContent.Should().Be("final-thinking");
    }

    [Fact]
    public async Task ExecuteAsync_WhenDsmlToolCallBlockedByHook_ShouldPropagateReasoningContent()
    {
        var dsmlContent = "I will search now.\n<function_calls><invoke name=\"echo\"><parameter name=\"q\">test</parameter></invoke></function_calls>";
        var provider = new QueueLLMProvider(
        [
            new LLMResponse { Content = dsmlContent, ReasoningContent = "blocked-dsml-thinking" },
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("echo", _ => "ok"));
        var hook = new BlockPostSamplingHook();
        var loop = NewToolCallLoop(tools, new AgentHookPipeline([hook]));
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = tools.GetAll() };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 2, CancellationToken.None);

        messages.Should().Contain(m => m.Role == "assistant" && m.ReasoningContent == "blocked-dsml-thinking");
    }

    [Fact]
    public async Task ExecuteAsync_WhenMaxRoundsExhaustedAndDsmlInFinalCall_ShouldPropagateReasoning()
    {
        var dsmlContent = "Final search.\n<function_calls><invoke name=\"echo\"><parameter name=\"q\">final</parameter></invoke></function_calls>";
        var provider = new QueueLLMProvider(
        [
            new LLMResponse { ToolCalls = [new ToolCall { Id = "tc-1", Name = "echo", ArgumentsJson = "{}" }] },
            new LLMResponse { Content = dsmlContent, ReasoningContent = "final-dsml-thinking" },
            new LLMResponse { Content = "summary", ReasoningContent = "summary-thinking" },
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("echo", _ => "ok"));
        var loop = NewToolCallLoop(tools);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = tools.GetAll() };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 1, CancellationToken.None);

        result.Should().Be("summary");
        var forwardedFinalDsmlAssistant = provider.Requests[2].Messages.Single(m =>
            m.Role == "assistant" &&
            m.ToolCalls is { Count: 1 } &&
            m.ToolCalls[0].Name == "echo" &&
            m.ReasoningContent == "final-dsml-thinking");
        forwardedFinalDsmlAssistant.ReasoningContent.Should().Be("final-dsml-thinking");
        var lastAssistant = messages.Last(m => m.Role == "assistant");
        lastAssistant.ReasoningContent.Should().Be("summary-thinking");
    }

    [Fact]
    public async Task ExecuteAsync_WhenFinalNoToolsCallContainsDsml_ShouldRejectToolAndNotExecuteAgain()
    {
        var finalDsml =
            "Final search.\n<function_calls><invoke name=\"echo\"><parameter name=\"q\">final</parameter></invoke></function_calls>";
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls = [new ToolCall { Id = "tc-initial", Name = "echo", ArgumentsJson = "{}" }],
            },
            new LLMResponse { Content = finalDsml },
            new LLMResponse { Content = "summary" },
        ]);
        var executions = 0;
        var exactTool = new DelegateTool("echo", _ =>
        {
            executions++;
            return "ok";
        });
        var tools = new ToolManager();
        tools.Register(exactTool);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };

        var result = await NewToolCallLoop(tools).ExecuteAsync(
            provider,
            messages,
            new LLMRequest { Messages = [], Tools = [exactTool] },
            maxRounds: 1,
            CancellationToken.None);

        result.Should().Be("summary");
        executions.Should().Be(1);
        messages.Where(message => message.Role == "tool").Should().SatisfyRespectively(
            initialResult => initialResult.Content.Should().Be("ok"),
            rejectedFinalResult => IsSafeRejectedToolFailure(rejectedFinalResult, "echo").Should().BeTrue());
        provider.Requests.Should().HaveCount(3);
        provider.Requests[1].Tools.Should().BeNull();
        provider.Requests[2].Tools.Should().BeNull();
    }

    [Theory]
    [InlineData("base64")]
    [InlineData("data")]
    public async Task ExecuteAsync_WhenToolReturnsLegacyRootImageAliases_ShouldPreserveImageContentParts(string payloadKey)
    {
        var provider = new QueueLLMProvider(
        [
            new LLMResponse
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tc-image",
                        Name = "image",
                        ArgumentsJson = "{}",
                    },
                ],
            },
            new LLMResponse { Content = "done" },
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("image", _ =>
            $$"""{"{{payloadKey}}":"Zm9v","media_type":"image/png","text":"diagram"}"""));
        var loop = NewToolCallLoop(tools);
        var messages = new List<ChatMessage> { ChatMessage.User("hello") };
        var request = new LLMRequest { Messages = [], Tools = tools.GetAll() };

        var result = await loop.ExecuteAsync(provider, messages, request, maxRounds: 2, CancellationToken.None);

        result.Should().Be("done");
        var toolMessage = messages.Single(m => m.Role == "tool" && m.ToolCallId == "tc-image");
        toolMessage.Content.Should().Be("diagram");
        toolMessage.ContentParts.Should().HaveCount(2);
        toolMessage.ContentParts![0].Kind.Should().Be(ContentPartKind.Text);
        toolMessage.ContentParts[0].Text.Should().Be("diagram");
        toolMessage.ContentParts[1].Kind.Should().Be(ContentPartKind.Image);
        toolMessage.ContentParts[1].DataBase64.Should().Be("Zm9v");
        toolMessage.ContentParts[1].MediaType.Should().Be("image/png");

        provider.Requests.Should().HaveCount(2);
        var forwardedToolMessage = provider.Requests[1].Messages.Single(m => m.Role == "tool" && m.ToolCallId == "tc-image");
        forwardedToolMessage.ContentParts.Should().HaveCount(2);
        forwardedToolMessage.ContentParts![1].Kind.Should().Be(ContentPartKind.Image);
        forwardedToolMessage.ContentParts[1].DataBase64.Should().Be("Zm9v");
    }

    private sealed class QueueLLMProvider : ILLMProvider
    {
        private readonly Queue<LLMResponse> _responses;

        public QueueLLMProvider(IEnumerable<LLMResponse> responses)
        {
            _responses = new Queue<LLMResponse>(responses);
        }

        public string Name => "queue";
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            var response = _responses.Count > 0 ? _responses.Dequeue() : new LLMResponse();

            if (!string.IsNullOrEmpty(response.ReasoningContent))
                yield return new LLMStreamChunk { DeltaReasoningContent = response.ReasoningContent };

            if (!string.IsNullOrEmpty(response.Content))
                yield return new LLMStreamChunk { DeltaContent = response.Content };

            if (response.ToolCalls is { Count: > 0 })
            {
                foreach (var toolCall in response.ToolCalls)
                    yield return new LLMStreamChunk { DeltaToolCall = toolCall };
            }

            yield return new LLMStreamChunk
            {
                IsLast = true,
                Usage = response.Usage,
                FinishReason = response.FinishReason,
            };
            await Task.CompletedTask;
        }
    }

    private static bool IsSafeRejectedToolFailure(ChatMessage message, string toolName) =>
        message.Role == "tool" &&
        message.Content == "{\"error\":\"The tool request failed.\"}" &&
        message.ToolResultView is
        {
            ToolName: var actualToolName,
            Failure:
            {
                Status: AgentToolReceiptStatus.Error,
                ErrorCode: "tool_execution_exception",
            },
        } &&
        string.Equals(actualToolName, toolName, StringComparison.Ordinal);

    private static LLMRequest CopyRequestWithTools(
        LLMRequest request,
        IReadOnlyList<IAgentTool>? tools) => new()
    {
        Messages = request.Messages,
        RequestId = request.RequestId,
        Metadata = request.Metadata,
        CallerContext = request.CallerContext,
        ToolContext = request.ToolContext,
        RoutingContext = request.RoutingContext,
        LlmControl = request.LlmControl,
        Tools = tools,
        Model = request.Model,
        Temperature = request.Temperature,
        MaxTokens = request.MaxTokens,
        ResponseFormat = request.ResponseFormat,
    };

    private static ToolCallLoop NewToolCallLoop(
        ToolManager tools,
        AgentHookPipeline? hooks = null,
        IReadOnlyList<ILLMCallMiddleware>? llmMiddlewares = null,
        TokenBudgetTracker? budgetTracker = null) =>
        new(
            tools,
            hooks,
            llmMiddlewares,
            budgetTracker,
            new TestExecutionPort());

    private sealed class DelegateTool : IAgentTool
    {
        private readonly Func<string, string> _execute;

        public DelegateTool(string name, Func<string, string> execute)
        {
            Name = name;
            _execute = execute;
        }

        public string Name { get; }
        public string Description => "delegate";
        public string ParametersSchema => "{}";
        public AgentToolReceipt? CreateSuccessReceipt(
            string callId,
            string toolName,
            string resultJson) =>
            new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = resultJson,
            };

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_execute(argumentsJson));
        }
    }

    private sealed class TestExecutionPort : IAgentToolExecutionPort
    {
        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            var safety = request.Tool.GetCallSafety(request.ArgumentsJson)
                ?? new AgentToolCallSafety(true, false, true);
            try
            {
                var result = await request.Tool.ExecuteAsync(request.ArgumentsJson, ct);
                var receipt = AgentToolReceiptFactory.CreateSuccess(
                    request.Tool,
                    request.ExecutionContext.Request.CallId ?? string.Empty,
                    request.Tool.Name,
                    safety,
                    result);
                return new AgentToolExecutionOutcome(
                    AgentToolExecutionOutcomeKind.Executed,
                    result,
                    receipt,
                    !safety.IsReadOnly,
                    string.Empty,
                    string.Empty,
                    AgentToolExecutionFailureStage.None,
                    TerminalInvoked: true,
                    Retryable: false,
                    AuditCompleted: true);
            }
            catch (Exception ex)
            {
                var result = ToolManager.BuildErrorJson("The tool request failed.");
                var receipt = AgentToolReceiptFactory.CreateError(
                    request.Tool,
                    request.ExecutionContext.Request.CallId ?? string.Empty,
                    request.Tool.Name,
                    safety,
                    result,
                    "tool_execution_exception",
                    ex.GetType().Name);
                return new AgentToolExecutionOutcome(
                    AgentToolExecutionOutcomeKind.Failed,
                    result,
                    receipt,
                    !safety.IsReadOnly,
                    "tool_execution_exception",
                    ex.GetType().Name,
                    AgentToolExecutionFailureStage.TerminalExecution,
                    TerminalInvoked: true,
                    Retryable: false,
                    AuditCompleted: true);
            }
        }
    }

    private sealed class DelegateLlmCallMiddleware(
        Func<LLMCallContext, Func<Task>, Task> handler) : ILLMCallMiddleware
    {
        public Task InvokeAsync(LLMCallContext context, Func<Task> next) => handler(context, next);
    }

    private sealed class CaptureLlmRequestIdentityMiddleware : ILLMCallMiddleware
    {
        public List<string> RequestIds { get; } = [];
        public List<string> CallIds { get; } = [];

        public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
        {
            if (context.Items.TryGetValue(LLMRequestMetadataKeys.RequestId, out var requestIdObj) &&
                requestIdObj is string requestId)
            {
                RequestIds.Add(requestId);
            }

            if (context.Items.TryGetValue(LLMRequestMetadataKeys.CallId, out var callIdObj) &&
                callIdObj is string callId)
            {
                CallIds.Add(callId);
            }

            await next();
        }
    }

    private sealed class BlockPostSamplingHook : IAIGAgentExecutionHook
    {
        public string Name => "block-post-sampling";
        public int Priority => 0;

        public Task OnPostSamplingAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            ctx.Items["block_tool_calls"] = true;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHook : IAIGAgentExecutionHook
    {
        public string Name => "rec";
        public int Priority => 0;

        public int LlmStartCount { get; private set; }
        public int LlmEndCount { get; private set; }
        public int ToolStartCount { get; private set; }
        public int ToolEndCount { get; private set; }
        public string? ToolResultAtEnd { get; private set; }
        public Action<AIGAgentExecutionHookContext>? OnToolStart { get; init; }

        public Task OnLLMRequestStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _ = ctx;
            LlmStartCount++;
            return Task.CompletedTask;
        }

        public Task OnLLMRequestEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _ = ctx;
            LlmEndCount++;
            return Task.CompletedTask;
        }

        public Task OnToolExecuteStartAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ToolStartCount++;
            OnToolStart?.Invoke(ctx);
            return Task.CompletedTask;
        }

        public Task OnToolExecuteEndAsync(AIGAgentExecutionHookContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ToolEndCount++;
            ToolResultAtEnd = ctx.ToolResult;
            return Task.CompletedTask;
        }
    }
}
