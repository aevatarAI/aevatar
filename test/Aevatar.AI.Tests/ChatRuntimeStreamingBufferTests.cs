using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

// Refactor (iter39/cluster-039-public-chatasync-adapter):
//   Old pattern: ChatRuntime 暴露 public ChatAsync 方法作为 non-streaming adapter,callers 可以选 non-streaming conversation API。
//   New principle: Public runtime surface 仅暴露 ChatStreamAsync;explicit offline aggregation 放到 narrowly named offline/test adapter(明确不能与 realtime chat 混淆)。Provider contract stream-only。
public sealed class ChatRuntimeStreamingBufferTests
{
    [Fact]
    public async Task ChatStreamAsync_WhenStreamOwnerHasNoBuffer_ShouldStillStreamAllChunks()
    {
        var provider = new StreamingProvider(["A", "B", "C", "D"]);
        var runtime = CreateRuntime(provider);

        var output = new StringBuilder();
        await foreach (var chunk in runtime.ChatStreamAsync("hello", turnCatalog: null))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
                output.Append(chunk.DeltaContent);
        }

        output.ToString().Should().Be("ABCD");
        provider.StreamCallCount.Should().Be(1);
    }

    [Fact]
    public void ChatRuntimeSource_ShouldNotReintroduceOwnedStreamLoop()
    {
        var root = FindRepositoryRoot();
        var chatRuntimeFile = Path.Combine(
            root,
            "src",
            "Aevatar.AI.Core",
            "Chat",
            "ChatRuntime.cs");
        var source = StripLineComments(File.ReadAllText(chatRuntimeFile));

        source.Should().NotContain("Task.Run");
        source.Should().NotContain("Channel<LLMStreamChunk>");
        source.Should().NotContain("ChannelWriter<LLMStreamChunk>");
        source.Should().NotContain("_streamBufferCapacity");
        source.Should().NotContain("streamBufferCapacity");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenProviderReturnsToolCallDelta_ShouldSurfaceStructuredChunks()
    {
        var provider = new StreamingProvider(["done"], streamToolCall: new ToolCall
        {
            Id = "tc-1",
            Name = "search",
            ArgumentsJson = "{\"q\":\"aevatar\"}",
        });
        var runtime = CreateRuntime(provider);
        var chunks = new List<LLMStreamChunk>();

        await foreach (var chunk in runtime.ChatStreamAsync("hello", maxToolRounds: 1, turnCatalog: null))
            chunks.Add(chunk);

        chunks.Should().Contain(x => x.DeltaToolCall != null);
        var toolCall = chunks.First(x => x.DeltaToolCall != null).DeltaToolCall!;
        toolCall.Id.Should().Be("tc-1");
        toolCall.Name.Should().Be("search");
        toolCall.ArgumentsJson.Should().Contain("aevatar");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenToolCallIdAppearsLate_ShouldPromoteToSingleFinalToolCall()
    {
        var provider = new StreamingProvider(
            chunks: ["done"],
            streamToolDeltas:
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = string.Empty,
                        Name = "search",
                        ArgumentsJson = "{\"q\":\"ae",
                    },
                },
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "tc-merge",
                        Name = string.Empty,
                        ArgumentsJson = "vatar\"}",
                    },
                },
            ]);
        var captureMiddleware = new CaptureLLMResponseMiddleware();
        var runtime = CreateRuntime(provider, llmMiddlewares: [captureMiddleware]);

        await foreach (var _ in runtime.ChatStreamAsync("hello", maxToolRounds: 1, turnCatalog: null))
        {
        }

        captureMiddleware.LastResponse.Should().NotBeNull();
        var toolCalls = captureMiddleware.LastResponse!.ToolCalls;
        toolCalls.Should().NotBeNull();
        toolCalls!.Should().ContainSingle();
        toolCalls[0].Id.Should().Be("tc-merge");
        toolCalls[0].Name.Should().Be("search");
        toolCalls[0].ArgumentsJson.Should().Be("{\"q\":\"aevatar\"}");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenProviderReturnsReasoningDelta_ShouldSurfaceReasoningChunk()
    {
        var provider = new StreamingProvider(
            chunks: [],
            streamToolDeltas:
            [
                new LLMStreamChunk
                {
                    DeltaReasoningContent = "thinking step",
                },
            ]);
        var runtime = CreateRuntime(provider);
        var chunks = new List<LLMStreamChunk>();

        await foreach (var chunk in runtime.ChatStreamAsync("hello", turnCatalog: null))
            chunks.Add(chunk);

        chunks.Should().Contain(x => x.DeltaReasoningContent == "thinking step");
    }

    [Fact]
    public async Task CreateStepExecutor_ShouldMatchChatStreamRequestIdentityAndFinalRoundToolRules()
    {
        var provider = new RecordingStepProvider();
        var tool = new CapturingTool();
        var tools = new ToolManager();
        tools.Register(tool);
        var baseToolContext = AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(null, null, null, 1_785_484_800_000),
            Caller = new AgentToolCallerContext("scope-base", "owner-base", "resp-base"),
            ExternalMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["safe"] = "tool-context",
            },
        };
        var runtime = CreateRuntime(
            provider,
            tools,
            requestBuilder: _ => new LLMRequest
            {
                Messages = [ChatMessage.System("system")],
                RequestId = "base-request",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [LLMRequestMetadataKeys.ScopeId] = "strip-me",
                    [LLMRequestMetadataKeys.NyxIdAccessToken] = "strip-token",
                    ["safe"] = "base",
                },
                CallerContext = new LLMRequestCallerContext(
                    "scope-1",
                    "owner-1",
                    "response-1",
                    new LLMRequestCallerCredentials("typed-bearer")),
                ToolContext = baseToolContext,
                Tools = [tool],
            });
        var executor = runtime.CreateStepExecutor(turnCatalog: null);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.CallId] = "strip-call",
            [LLMRequestMetadataKeys.ModelOverride] = "strip-model",
            ["safe"] = "override",
        };
        var llmControl = LLMControlContext.Empty with
        {
            ModelOverride = "model-a",
            NyxIdRoutePreference = "route-a",
            MaxToolRoundsOverride = 1,
        };

        var stepRequest = executor.BuildLlmStepRequest(
            [ChatMessage.User("hello")],
            "req-123",
            metadata,
            toolContext: null,
            llmControl,
            round: 0,
            finalNoTools: false);

        stepRequest.RequestId.Should().Be("req-123");
        stepRequest.CallerContext.Should().BeEquivalentTo(new LLMRequestCallerContext(
            "scope-1",
            "owner-1",
            "response-1",
            new LLMRequestCallerCredentials("typed-bearer")));
        stepRequest.Metadata.Should().BeEquivalentTo(new Dictionary<string, string> { ["safe"] = "override" });
        stepRequest.ToolContext.Should().NotBeNull();
        stepRequest.ToolContext!.Request.RequestId.Should().Be("req-123");
        stepRequest.ToolContext.Request.CallId.Should().Be("req-123");
        stepRequest.ToolContext.Routing.ModelOverride.Should().Be("model-a");
        stepRequest.ToolContext.Routing.NyxIdRoutePreference.Should().Be("route-a");
        stepRequest.Tools.Should().ContainSingle().Which.Name.Should().Be("capture");

        var chunks = new List<LLMStreamChunk>();
        var stepResult = await executor.ExecuteLlmStepAsync(
            executor.ResolveProvider(),
            stepRequest,
            (chunk, _) =>
            {
                chunks.Add(chunk);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        stepResult.Content.Should().Be("answer");
        stepResult.FinishReason.Should().Be("stop");
        chunks.Should().ContainSingle(chunk => chunk.DeltaContent == "answer");
        provider.Requests.Should().ContainSingle();
        provider.Requests[0].Should().BeSameAs(stepRequest);

        var finalRequest = executor.BuildLlmStepRequest(
            [ChatMessage.User("hello"), ChatMessage.Assistant("partial")],
            "req-123",
            metadata,
            toolContext: null,
            llmControl,
            round: 1,
            finalNoTools: true,
            toolReceipts: null);

        finalRequest.Tools.Should().BeNull();
        finalRequest.ToolContext!.Request.CallId.Should().Be("req-123:final");

        var runtimeChunks = new List<LLMStreamChunk>();
        await foreach (var chunk in runtime.ChatStreamAsync(
                           [ContentPart.TextPart("hello")],
                           maxToolRounds: 1,
                           requestId: "req-123",
                           llmControl,
                           toolContext: null,
                           turnCatalog: null,
                           metadata,
                           CancellationToken.None))
        {
            runtimeChunks.Add(chunk);
        }

        provider.Requests.Should().HaveCount(2);
        provider.Requests[1].RequestId.Should().Be(stepRequest.RequestId);
        provider.Requests[1].Metadata.Should().BeEquivalentTo(stepRequest.Metadata);
        provider.Requests[1].CallerContext.Should().BeEquivalentTo(stepRequest.CallerContext);
        provider.Requests[1].ToolContext.Should().BeEquivalentTo(stepRequest.ToolContext);
        provider.Requests[1].Tools.Should().ContainSingle().Which.Name.Should().Be("capture");
        runtimeChunks.Should().ContainSingle(chunk => chunk.DeltaContent == "answer");

        var toolResults = await executor.ExecuteToolStepAsync(
            [new ToolCall { Id = "tool-1", Name = "capture", ArgumentsJson = """{"x":1}""" }],
            metadata,
            stepRequest.ToolContext,
            CancellationToken.None);

        toolResults.Should().ContainSingle();
        toolResults[0].CallId.Should().Be("tool-1");
        toolResults[0].Result.Should().Contain("tool-ok");
        tool.CapturedContext.Should().NotBeNull();
        tool.CapturedContext!.Request.CallId.Should().Be("tool-1");
        tool.CapturedContext.Request.RequestId.Should().Be("req-123");
        tool.CapturedContext.Credentials.NyxIdAccessToken.Should().BeNull();
        tool.CapturedContext.Routing.ModelOverride.Should().Be("model-a");
        tool.CapturedContext.ExternalMetadata["safe"].Should().Be("tool-context");
        tool.CapturedContext.ExternalMetadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdAccessToken);
        tool.CapturedContext.ExternalMetadata.Should().NotContainKey(LLMRequestMetadataKeys.ModelOverride);
    }

    [Fact]
    public void BuildLlmStepRequest_WhenFinalNoToolsWithoutMutatingReceipt_ShouldInjectConstraintWithoutMutatingCallerMessages()
    {
        var provider = new RecordingStepProvider();
        var runtime = CreateRuntime(provider);
        var executor = runtime.CreateStepExecutor(turnCatalog: null);
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("system"),
            ChatMessage.User("hello"),
        };

        var request = executor.BuildLlmStepRequest(
            messages,
            "req-final",
            metadata: null,
            toolContext: null,
            llmControl: null,
            round: 1,
            finalNoTools: true);

        request.Messages.Should().HaveCount(3);
        request.Messages.Last().Role.Should().Be("system");
        request.Messages.Last().Content.Should().Contain("no successful mutating tool execution");
        messages.Should().HaveCount(2);
    }

    [Fact]
    public void BuildLlmStepRequest_WhenFinalNoToolsHasMutatingSuccessReceipt_ShouldNotInjectConstraint()
    {
        var provider = new RecordingStepProvider();
        var runtime = CreateRuntime(provider);
        var executor = runtime.CreateStepExecutor(turnCatalog: null);
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("system"),
            ChatMessage.User("hello"),
        };

        var request = executor.BuildLlmStepRequest(
            messages,
            "req-final",
            metadata: null,
            toolContext: null,
            llmControl: null,
            round: 1,
            finalNoTools: true,
            toolReceipts:
            [
                new AgentToolReceipt
                {
                    Status = AgentToolReceiptStatus.Success,
                    SideEffectKind = "definition.update",
                },
            ]);

        request.Messages.Should().BeEquivalentTo(messages);
    }

    [Fact]
    public async Task ExecuteToolStepAsync_WhenToolContextIsNull_ShouldNotPromoteRequestMetadataToToolControl()
    {
        var provider = new RecordingStepProvider();
        var tool = new CapturingTool();
        var tools = new ToolManager();
        tools.Register(tool);
        var runtime = CreateRuntime(provider, tools);
        var executor = runtime.CreateStepExecutor(turnCatalog: null);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "metadata-token",
            [LLMRequestMetadataKeys.ModelOverride] = "metadata-model",
            [LLMRequestMetadataKeys.CallId] = "metadata-call",
            ["trace-id"] = "trace-1",
        };

        var toolResults = await executor.ExecuteToolStepAsync(
            [new ToolCall { Id = "tool-1", Name = "capture", ArgumentsJson = "{}" }],
            metadata,
            toolContext: null,
            CancellationToken.None);

        toolResults.Should().ContainSingle();
        tool.CapturedContext.Should().NotBeNull();
        tool.CapturedContext!.Request.CallId.Should().Be("tool-1");
        tool.CapturedContext.Request.OperationId.Should().NotBeNullOrWhiteSpace();
        tool.CapturedContext.Credentials.Should().Be(AgentToolCredentials.Empty);
        tool.CapturedContext.Routing.Should().Be(LLMRequestRoutingContext.Empty);
        tool.CapturedContext.ExternalMetadata.Should().BeEmpty();
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public void CreateStepExecutor_BuildBaseRequest_ShouldMergeOverrideMetadataThenScrubOwnedKeys()
    {
        var provider = new RecordingStepProvider();
        var runtime = CreateRuntime(
            provider,
            requestBuilder: _ => new LLMRequest
            {
                Messages = [],
                RequestId = "base-request",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["safe"] = "base",
                    ["override"] = "base",
                    [LLMRequestMetadataKeys.NyxIdAccessToken] = "base-token",
                    [LLMRequestMetadataKeys.ModelOverride] = "base-model",
                },
            });
        var executor = runtime.CreateStepExecutor(turnCatalog: null);

        var request = executor.BuildBaseRequest(
            " request-1 ",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["override"] = "override",
                [LLMRequestMetadataKeys.NyxIdAccessToken] = "override-token",
                [LLMRequestMetadataKeys.CallId] = "override-call",
            },
            toolContext: null,
            llmControl: null);

        request.RequestId.Should().Be("request-1");
        request.ToolContext!.Request.RequestId.Should().Be("request-1");
        request.ToolContext.Credentials.NyxIdAccessToken.Should().BeNull();
        request.ToolContext.Routing.ModelOverride.Should().BeNull();
        request.Metadata.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["safe"] = "base",
            ["override"] = "override",
        });
    }

    [Fact]
    public void CreateStepExecutor_BuildBaseRequest_WhenIssuedAtMissing_ShouldSetCurrentTimestamp()
    {
        var provider = new RecordingStepProvider();
        var runtime = CreateRuntime(
            provider,
            requestBuilder: _ => new LLMRequest
            {
                Messages = [],
                ToolContext = AgentToolExecutionContext.Empty with
                {
                    Request = new AgentToolRequestIdentity(null, null, null, 0),
                },
            });
        var executor = runtime.CreateStepExecutor(turnCatalog: null);
        var earliestIssuedAt = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();

        var request = executor.BuildBaseRequest(
            requestId: "request-missing-issued-at",
            metadata: null,
            toolContext: null,
            llmControl: null);

        var latestIssuedAt = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        request.ToolContext!.Request.IssuedAtUnixMs.Should().BeInRange(earliestIssuedAt, latestIssuedAt);
        request.ToolContext.Request.IssuedAtUnixMs.Should().BePositive();
    }

    [Fact]
    public void CreateStepExecutor_BuildBaseRequest_WhenIssuedAtExists_ShouldPreserveTimestamp()
    {
        const long issuedAtUnixMs = 1_700_000_000_123;
        var provider = new RecordingStepProvider();
        var runtime = CreateRuntime(
            provider,
            requestBuilder: _ => new LLMRequest
            {
                Messages = [],
                ToolContext = AgentToolExecutionContext.Empty with
                {
                    Request = new AgentToolRequestIdentity(null, null, null, issuedAtUnixMs),
                },
            });
        var executor = runtime.CreateStepExecutor(turnCatalog: null);

        var request = executor.BuildBaseRequest(
            requestId: "request-existing-issued-at",
            metadata: null,
            toolContext: null,
            llmControl: null);

        request.ToolContext!.Request.IssuedAtUnixMs.Should().Be(issuedAtUnixMs);
    }

    [Fact]
    public void CreateStepExecutor_BuildBaseRequest_WhenExternalOwnerConflicts_ShouldKeepBaseOwner()
    {
        var provider = new RecordingStepProvider();
        var runtime = CreateRuntime(
            provider,
            requestBuilder: _ => new LLMRequest
            {
                Messages = [],
                ToolContext = AgentToolExecutionContext.Empty with
                {
                    ExecutionOwner = AgentToolExecutionOwners.Actor("actor-server-owned"),
                },
            });
        var executor = runtime.CreateStepExecutor(turnCatalog: null);
        var externalToolContext = AgentToolExecutionContext.Empty with
        {
            ExecutionOwner = AgentToolExecutionOwners.HostService("external-host-owner"),
        };

        var request = executor.BuildBaseRequest(
            requestId: "request-owner-precedence",
            metadata: null,
            toolContext: externalToolContext,
            llmControl: null);

        request.ToolContext!.ExecutionOwner.Kind.Should().Be(AgentToolExecutionOwnerKind.Actor);
        request.ToolContext.ExecutionOwner.OwnerId.Should().Be("actor-server-owned");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenStreamReturnsToolCall_ShouldExecuteToolAndContinueWithFollowUpRound()
    {
        var provider = new QueuedStreamingProvider(
        [
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "tc-follow-up",
                        Name = "lookup",
                        ArgumentsJson = "{\"q\":\"lark\"}",
                    },
                },
            ],
            [
                new LLMStreamChunk { DeltaContent = "tool-finished" },
            ],
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("lookup", args => $"RESULT:{args}"));
        var runtime = CreateRuntime(provider, tools: tools);
        var output = new StringBuilder();

        await foreach (var chunk in runtime.ChatStreamAsync("hello", maxToolRounds: 2, turnCatalog: null))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
                output.Append(chunk.DeltaContent);
        }

        output.ToString().Should().Be("tool-finished");
        provider.StreamRequests.Should().HaveCount(2);
        provider.StreamRequests[1].Messages.Any(m =>
            m.Role == "assistant" &&
            m.ToolCalls != null &&
            m.ToolCalls.Count == 1 &&
            m.ToolCalls[0].Id == "tc-follow-up" &&
            m.ToolCalls[0].Name == "lookup" &&
            m.ToolCalls[0].ArgumentsJson == "{\"q\":\"lark\"}").Should().BeTrue();
        provider.StreamRequests[1].Messages.Any(m =>
            m.Role == "tool" &&
            m.ToolCallId == "tc-follow-up" &&
            m.Content == "RESULT:{\"q\":\"lark\"}").Should().BeTrue();
    }

    [Theory]
    [InlineData(AgentToolReceiptStatus.Error)]
    [InlineData(AgentToolReceiptStatus.Denied)]
    public async Task ChatStreamAsync_WhenToolReceiptFails_ShouldRedactArgumentsBeforeFollowUpRound(
        AgentToolReceiptStatus receiptStatus)
    {
        const string secretArguments =
            "{\"slug\":\"api-github\",\"path\":\"/repos/private?access_token=query-secret\",\"headers\":{\"X-Credential\":\"header-secret\"}}";
        var providerToolCall = new ToolCall
        {
            Id = "tc-sensitive",
            Name = "secure_lookup",
            ArgumentsJson = secretArguments,
        };
        var provider = new QueuedStreamingProvider(
        [
            [
                new LLMStreamChunk { DeltaContent = "checking access" },
                new LLMStreamChunk { DeltaToolCall = providerToolCall },
            ],
            [new LLMStreamChunk { DeltaContent = "safe follow-up" }],
        ]);
        var tools = new ToolManager();
        tools.Register(new ReceiptTool("secure_lookup", receiptStatus));
        var runtime = CreateRuntime(provider, tools: tools);

        await foreach (var _ in runtime.ChatStreamAsync("hello", maxToolRounds: 2, turnCatalog: null))
        {
        }

        provider.StreamRequests.Should().HaveCount(2);
        var followUpMessages = provider.StreamRequests[1].Messages;
        var assistant = followUpMessages.Should().ContainSingle(message =>
            message.Role == "assistant" && message.ToolCalls != null && message.ToolCalls.Count == 1).Which;
        assistant.Content.Should().Be("checking access");
        assistant.ToolCalls![0].Id.Should().Be("tc-sensitive");
        assistant.ToolCalls[0].Name.Should().Be("secure_lookup");
        assistant.ToolCalls[0].ArgumentsJson.Should()
            .NotContain("query-secret")
            .And.NotContain("header-secret");
        assistant.ToolCalls[0].ArgumentsJson.Should().Be("{}");
        followUpMessages.Should().ContainSingle(message =>
            message.Role == "tool" &&
            message.ToolCallId == "tc-sensitive" &&
            message.Content == "{\"error\":\"safe tool failure\"}");
        followUpMessages
            .SelectMany(message => message.ToolCalls ?? [])
            .Select(call => call.ArgumentsJson)
            .Should().NotContain(arguments =>
                arguments.Contains("query-secret", StringComparison.Ordinal) ||
                arguments.Contains("header-secret", StringComparison.Ordinal));
        providerToolCall.ArgumentsJson.Should().Be(secretArguments);
    }

    [Fact]
    public async Task ChatStreamAsync_WhenAuthorizationBlocksFirstOfMultipleCalls_ShouldReconcileSafeTranscript()
    {
        var provider = new QueuedStreamingProvider(
        [
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "tc-auth",
                        Name = "authorization_tool",
                        ArgumentsJson = "{\"token\":\"authorization-secret\"}",
                    },
                },
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "tc-after-auth",
                        Name = "queued_tool",
                        ArgumentsJson = "{\"token\":\"queued-secret\"}",
                    },
                },
            ],
            [new LLMStreamChunk { DeltaContent = "later answer" }],
        ]);
        var tools = new ToolManager();
        tools.Register(new ReceiptTool("authorization_tool", AgentToolReceiptStatus.AuthorizationRequired));
        tools.Register(new DelegateTool("queued_tool", _ => "should-not-run"));
        var runtime = CreateRuntime(provider, tools: tools);

        await foreach (var _ in runtime.ChatStreamAsync("blocked turn", maxToolRounds: 2, turnCatalog: null))
        {
        }

        await foreach (var _ in runtime.ChatStreamAsync("later turn", maxToolRounds: 1, turnCatalog: null))
        {
        }

        provider.StreamRequests.Should().HaveCount(2);
        var laterMessages = provider.StreamRequests[1].Messages;
        var assistant = laterMessages.Should().ContainSingle(message =>
            message.Role == "assistant" && message.ToolCalls != null && message.ToolCalls.Count == 2).Which;
        assistant.ToolCalls!.Select(call => (call.Id, call.Name)).Should().Equal(
            ("tc-auth", "authorization_tool"),
            ("tc-after-auth", "queued_tool"));
        assistant.ToolCalls!.Select(call => call.ArgumentsJson).Should().OnlyContain(arguments => arguments == "{}");
        laterMessages.Where(message => message.Role == "tool")
            .Select(message => message.ToolCallId)
            .Should().Equal("tc-auth", "tc-after-auth");
        laterMessages
            .SelectMany(message => message.ToolCalls ?? [])
            .Select(call => call.ArgumentsJson)
            .Should().NotContain(arguments => arguments.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatStreamAsync_WhenToolCallRoundHasReasoning_ShouldPreserveItInFollowUpRequest()
    {
        var provider = new QueuedStreamingProvider(
        [
            [
                new LLMStreamChunk { DeltaReasoningContent = "thinking-before-tool" },
                new LLMStreamChunk { DeltaContent = "checking" },
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "tc-reasoning",
                        Name = "lookup",
                        ArgumentsJson = "{\"q\":\"sg\"}",
                    },
                },
            ],
            [
                new LLMStreamChunk { DeltaContent = "done" },
            ],
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("lookup", args => $"RESULT:{args}"));
        var runtime = CreateRuntime(provider, tools: tools);

        await foreach (var _ in runtime.ChatStreamAsync("hello", maxToolRounds: 2, turnCatalog: null))
        {
        }

        provider.StreamRequests.Should().HaveCount(2);
        var assistantToolCallMessage = provider.StreamRequests[1].Messages.Single(m =>
            m.Role == "assistant" &&
            m.ToolCalls is { Count: 1 } &&
            m.ToolCalls[0].Id == "tc-reasoning");
        assistantToolCallMessage.Content.Should().Be("checking");
        assistantToolCallMessage.ReasoningContent.Should().Be("thinking-before-tool");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenTextToolCallRoundHasReasoning_ShouldPreserveItInFollowUpRequest()
    {
        var provider = new QueuedStreamingProvider(
        [
            [
                new LLMStreamChunk { DeltaReasoningContent = "thinking-before-text-tool" },
                new LLMStreamChunk
                {
                    DeltaContent = """
                        I will search now.
                        <function_calls>
                        <invoke name="lookup">
                        <parameter name="q">lark</parameter>
                        </invoke>
                        </function_calls>
                        """,
                },
            ],
            [
                new LLMStreamChunk { DeltaContent = "done" },
            ],
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("lookup", args => $"RESULT:{args}"));
        var runtime = CreateRuntime(provider, tools: tools);

        await foreach (var _ in runtime.ChatStreamAsync("hello", maxToolRounds: 2, turnCatalog: null))
        {
        }

        provider.StreamRequests.Should().HaveCount(2);
        var assistantToolCallMessage = provider.StreamRequests[1].Messages.Single(m =>
            m.Role == "assistant" &&
            m.ToolCalls is { Count: 1 } &&
            m.ToolCalls[0].Name == "lookup");
        assistantToolCallMessage.Content.Should().Be("I will search now.");
        assistantToolCallMessage.ReasoningContent.Should().Be("thinking-before-text-tool");
        provider.StreamRequests[1].Messages.Count(m =>
            m.Role == "assistant" &&
            m.ToolCalls is { Count: > 0 }).Should().Be(1);
    }

    [Fact]
    public async Task ChatStreamAsync_WhenTextToolReceiptFails_ShouldRedactArgumentsBeforeFollowUpRound()
    {
        var provider = new QueuedStreamingProvider(
        [
            [new LLMStreamChunk
            {
                DeltaContent = """
                    <function_calls>
                    <invoke name="secure_lookup">
                    <parameter name="path">/repos/private?access_token=text-secret</parameter>
                    </invoke>
                    </function_calls>
                    """,
            }],
            [new LLMStreamChunk { DeltaContent = "safe follow-up" }],
        ]);
        var tools = new ToolManager();
        tools.Register(new ReceiptTool("secure_lookup", AgentToolReceiptStatus.Error));
        var runtime = CreateRuntime(provider, tools: tools);

        await foreach (var _ in runtime.ChatStreamAsync("hello", maxToolRounds: 2, turnCatalog: null))
        {
        }

        var followUpMessages = provider.StreamRequests.Should().HaveCount(2).And.Subject.Last().Messages;
        var assistant = followUpMessages.Should().ContainSingle(message =>
            message.Role == "assistant" && message.ToolCalls != null && message.ToolCalls.Count == 1).Which;
        assistant.ToolCalls![0].Name.Should().Be("secure_lookup");
        assistant.ToolCalls[0].ArgumentsJson.Should().Be("{}");
        followUpMessages.Should().ContainSingle(message =>
            message.Role == "tool" && message.ToolCallId == assistant.ToolCalls[0].Id);
        followUpMessages
            .SelectMany(message => message.ToolCalls ?? [])
            .Select(call => call.ArgumentsJson)
            .Should().NotContain(arguments => arguments.Contains("text-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatStreamAsync_WhenFinalRoundParsesTextToolCall_ShouldRejectToolAbsentFromFinalRequest()
    {
        var provider = new QueuedStreamingProvider(
        [
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "tc-initial",
                        Name = "lookup",
                        ArgumentsJson = "{\"q\":\"initial\"}",
                    },
                },
            ],
            [
                new LLMStreamChunk { DeltaReasoningContent = "thinking-before-final-text-tool" },
                new LLMStreamChunk
                {
                    DeltaContent = """
                        <function_calls>
                        <invoke name="lookup">
                        <parameter name="q">final</parameter>
                        </invoke>
                        </function_calls>
                        """,
                },
            ],
            [
                new LLMStreamChunk { DeltaContent = "summary-ready" },
            ],
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("lookup", args => $"RESULT:{args}"));
        var runtime = CreateRuntime(provider, tools: tools);

        var output = new StringBuilder();
        await foreach (var chunk in runtime.ChatStreamAsync("hello", maxToolRounds: 1, turnCatalog: null))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
                output.Append(chunk.DeltaContent);
        }

        output.ToString().Should().Contain("summary-ready");
        provider.StreamRequests.Should().HaveCount(3);
        provider.StreamRequests[2].Messages.Should().Contain(m =>
            IsSafeRejectedToolFailure(m, "lookup"));
        provider.StreamRequests[2].Messages.Should().NotContain(m =>
            m.Role == "tool" &&
            m.Content == "RESULT:{\"q\":\"final\"}");
        var assistantToolCallMessage = provider.StreamRequests[2].Messages.Single(m =>
            m.Role == "assistant" &&
            m.ToolCalls is { Count: 1 } &&
            m.ToolCalls[0].Name == "lookup" &&
            m.ReasoningContent == "thinking-before-final-text-tool");
        assistantToolCallMessage.ReasoningContent.Should().Be("thinking-before-final-text-tool");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenFinalTextToolReceiptFails_ShouldRedactArgumentsBeforeSummaryRequest()
    {
        var provider = new QueuedStreamingProvider(
        [
            [new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "tc-initial-success",
                    Name = "lookup",
                    ArgumentsJson = "{\"q\":\"initial\"}",
                },
            }],
            [new LLMStreamChunk
            {
                DeltaContent = """
                    <function_calls>
                    <invoke name="secure_lookup">
                    <parameter name="path">/repos/private?access_token=final-text-secret</parameter>
                    </invoke>
                    </function_calls>
                    """,
            }],
            [new LLMStreamChunk { DeltaContent = "summary-ready" }],
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("lookup", _ => "initial-result"));
        tools.Register(new ReceiptTool("secure_lookup", AgentToolReceiptStatus.Error));
        var runtime = CreateRuntime(provider, tools: tools);

        await foreach (var _ in runtime.ChatStreamAsync("hello", maxToolRounds: 1, turnCatalog: null))
        {
        }

        var summaryMessages = provider.StreamRequests.Should().HaveCount(3).And.Subject.Last().Messages;
        var assistant = summaryMessages.Should().ContainSingle(message =>
            message.Role == "assistant" &&
            message.ToolCalls != null &&
            message.ToolCalls.Count == 1 &&
            message.ToolCalls[0].Name == "secure_lookup").Which;
        assistant.ToolCalls![0].ArgumentsJson.Should().Be("{}");
        summaryMessages.Should().ContainSingle(message =>
            message.Role == "tool" && message.ToolCallId == assistant.ToolCalls[0].Id);
        summaryMessages
            .SelectMany(message => message.ToolCalls ?? [])
            .Select(call => call.ArgumentsJson)
            .Should().NotContain(arguments => arguments.Contains("final-text-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatStreamAsync_WhenFinalTextToolHasNoMutatingSuccess_ShouldInjectSummaryConstraint()
    {
        var provider = new QueuedStreamingProvider(
        [
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "tc-initial-read",
                        Name = "lookup",
                        ArgumentsJson = "{\"q\":\"initial\"}",
                    },
                },
            ],
            [
                new LLMStreamChunk
                {
                    DeltaContent = """
                        <function_calls>
                        <invoke name="lookup">
                        <parameter name="q">final</parameter>
                        </invoke>
                        </function_calls>
                        """,
                },
            ],
            [
                new LLMStreamChunk { DeltaContent = "summary-ready" },
            ],
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("lookup", args => $"RESULT:{args}", isReadOnly: true));
        var runtime = CreateRuntime(provider, tools: tools);

        await foreach (var _ in runtime.ChatStreamAsync("hello", maxToolRounds: 1, turnCatalog: null))
        {
        }

        provider.StreamRequests.Should().HaveCount(3);
        provider.StreamRequests[2].Messages
            .Where(message => message.Role == "system" &&
                              message.Content?.Contains("no successful mutating tool execution") == true)
            .Should().ContainSingle();
    }

    [Fact]
    public async Task ChatStreamAsync_WhenFinalTextToolIsAbsentFromFinalRequest_ShouldInjectSummaryConstraint()
    {
        var provider = new QueuedStreamingProvider(
        [
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "tc-initial-read",
                        Name = "lookup",
                        ArgumentsJson = "{\"q\":\"initial\"}",
                    },
                },
            ],
            [
                new LLMStreamChunk
                {
                    DeltaContent = """
                        <function_calls>
                        <invoke name="write_config">
                        <parameter name="value">new</parameter>
                        </invoke>
                        </function_calls>
                        """,
                },
            ],
            [
                new LLMStreamChunk { DeltaContent = "summary-ready" },
            ],
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("lookup", args => $"RESULT:{args}", isReadOnly: true));
        tools.Register(new DelegateTool("write_config", args => $"RESULT:{args}"));
        var runtime = CreateRuntime(provider, tools: tools);

        await foreach (var _ in runtime.ChatStreamAsync("hello", maxToolRounds: 1, turnCatalog: null))
        {
        }

        provider.StreamRequests.Should().HaveCount(3);
        provider.StreamRequests[2].Messages
            .Where(message => message.Role == "system" &&
                              message.Content?.Contains("no successful mutating tool execution") == true)
            .Should().ContainSingle();
    }

    [Fact]
    public async Task ChatStreamAsync_WhenFinalNoToolsHasNoMutatingSuccess_ShouldInjectEphemeralConstraint()
    {
        var provider = new QueuedStreamingProvider(
        [
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "tc-read",
                        Name = "lookup",
                        ArgumentsJson = "{\"q\":\"status\"}",
                    },
                },
            ],
            [
                new LLMStreamChunk { DeltaContent = "final-without-mutation" },
            ],
            [
                new LLMStreamChunk { DeltaContent = "second-turn" },
            ],
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("lookup", args => $"RESULT:{args}", isReadOnly: true));
        var runtime = CreateRuntime(provider, tools: tools);

        await foreach (var _ in runtime.ChatStreamAsync("hello", maxToolRounds: 1, turnCatalog: null))
        {
        }

        provider.StreamRequests.Should().HaveCount(2);
        provider.StreamRequests[1].Messages
            .Where(message => message.Role == "system" &&
                              message.Content?.Contains("no successful mutating tool execution") == true)
            .Should().ContainSingle();

        await foreach (var _ in runtime.ChatStreamAsync("next", maxToolRounds: 1, turnCatalog: null))
        {
        }

        provider.StreamRequests.Should().HaveCount(3);
        provider.StreamRequests[2].Messages
            .Where(message => message.Role == "system" &&
                              message.Content?.Contains("no successful mutating tool execution") == true)
            .Should().BeEmpty("the guard is request-local and must not be persisted to history");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenFinalNoToolsHasMutatingSuccess_ShouldNotInjectConstraint()
    {
        var provider = new QueuedStreamingProvider(
        [
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "tc-write",
                        Name = "write_config",
                        ArgumentsJson = "{\"value\":\"new\"}",
                    },
                },
            ],
            [
                new LLMStreamChunk { DeltaContent = "final-after-mutation" },
            ],
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("write_config", args => $"RESULT:{args}"));
        var runtime = CreateRuntime(provider, tools: tools);

        await foreach (var _ in runtime.ChatStreamAsync("hello", maxToolRounds: 1, turnCatalog: null))
        {
        }

        provider.StreamRequests.Should().HaveCount(2);
        provider.StreamRequests[1].Messages
            .Where(message => message.Role == "system" &&
                              message.Content?.Contains("no successful mutating tool execution") == true)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public async Task ChatStreamAsync_WhenOutcomeToolsShareName_ShouldClassifyRequestLocalTool(
        bool globalIsReadOnly,
        bool requestIsReadOnly,
        bool expectNoMutationConstraint)
    {
        var provider = new QueuedStreamingProvider(
        [
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "tc-shared",
                        Name = "shared_tool",
                        ArgumentsJson = "{}",
                    },
                },
            ],
            [
                new LLMStreamChunk { DeltaContent = "final" },
            ],
        ]);
        var globalExecutionCount = 0;
        var requestExecutionCount = 0;
        var globalTools = new ToolManager();
        globalTools.Register(new DelegateTool(
            "shared_tool",
            _ =>
            {
                globalExecutionCount++;
                return "global";
            },
            isReadOnly: globalIsReadOnly));
        var requestTool = new DelegateTool(
            "shared_tool",
            _ =>
            {
                requestExecutionCount++;
                return "request-local";
            },
            isReadOnly: requestIsReadOnly);
        var runtime = CreateRuntime(
            provider,
            globalTools,
            requestBuilder: _ => new LLMRequest
            {
                Messages = [],
                Tools = [requestTool],
            });

        await foreach (var _ in runtime.ChatStreamAsync("hello", maxToolRounds: 1, turnCatalog: null))
        {
        }

        globalExecutionCount.Should().Be(0);
        requestExecutionCount.Should().Be(1);
        provider.StreamRequests.Should().HaveCount(2);
        var constraints = provider.StreamRequests[1].Messages.Where(message =>
            message.Role == "system" &&
            message.Content?.Contains("no successful mutating tool execution") == true);
        constraints.Should().HaveCount(expectNoMutationConstraint ? 1 : 0);
    }

    [Fact]
    public async Task ChatStreamAsync_WhenFinalRoundParsesTextToolCall_ShouldUseOnlyFinalRequestCapabilities()
    {
        var provider = new QueuedStreamingProvider(
        [
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "tc-initial",
                        Name = "lookup",
                        ArgumentsJson = "{\"q\":\"initial\"}",
                    },
                },
            ],
            [
                new LLMStreamChunk
                {
                    DeltaContent = """
                        <function_calls>
                        <invoke name="lookup">
                        <parameter name="q">final</parameter>
                        </invoke>
                        </function_calls>
                        """,
                },
            ],
            [
                new LLMStreamChunk { DeltaContent = "summary-ready" },
            ],
        ]);
        var tools = new ToolManager();
        tools.Register(new DelegateTool("lookup", _ => string.Join(
            "|",
            AgentToolRequestContext.NyxIdAccessToken,
            AgentToolRequestContext.ScopeId,
            AgentToolRequestContext.CallId,
            AgentToolRequestContext.ChannelMessageId)));
        var runtime = CreateRuntime(
            provider,
            tools: tools,
            requestBuilder: _ => new LLMRequest
            {
                Messages = [],
                Tools = tools.GetAll(),
                ToolContext = AgentToolExecutionContext.Empty with
                {
                    Credentials = new AgentToolCredentials("typed-access", null, null),
                    Caller = new AgentToolCallerContext("typed-scope", null, null),
                    Channel = new AgentToolChannelContext(null, null, null, "typed-message", null),
                },
            });

        await foreach (var _ in runtime.ChatStreamAsync(
                           "hello",
                           maxToolRounds: 1,
                           requestId: "request-typed",
                           turnCatalog: null))
        {
        }

        provider.StreamRequests.Should().HaveCount(3);
        provider.StreamRequests[0].Metadata.Should().BeEmpty();
        provider.StreamRequests[1].Metadata.Should().BeEmpty();
        provider.StreamRequests[2].Messages.Should().Contain(m =>
            m.Role == "tool" &&
            m.Content != null &&
            m.Content.StartsWith("typed-access|typed-scope|tc-initial", StringComparison.Ordinal) &&
            m.Content.EndsWith("|typed-message", StringComparison.Ordinal));
        provider.StreamRequests[2].Messages.Should().Contain(m =>
            m.ToolCallId != null &&
            m.ToolCallId.StartsWith("text-tc-", StringComparison.Ordinal) &&
            IsSafeRejectedToolFailure(m, "lookup"));
    }

    [Fact]
    public async Task ChatStreamAsync_WhenRequestIdentityProvided_ShouldForwardRequestIdAndMergeMetadata()
    {
        var provider = new StreamingProvider(["A"]);
        var runtime = CreateRuntime(
            provider,
            requestBuilder: _ => new LLMRequest
            {
                Messages = [],
                RequestId = "base-request",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["base"] = "1",
                    ["override"] = "old",
                },
            });

        var providerMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["override"] = "new",
            ["workflow.run_id"] = "run-1",
        };

        await foreach (var _ in runtime.ChatStreamAsync(
                           "hello",
                           "session-42",
                           turnCatalog: null,
                           metadata: providerMetadata))
        {
        }

        provider.LastStreamRequest.Should().NotBeNull();
        provider.LastStreamRequest!.RequestId.Should().Be("session-42");
        provider.LastStreamRequest.Metadata.Should().NotBeNull();
        provider.LastStreamRequest.Metadata!["base"].Should().Be("1");
        provider.LastStreamRequest.Metadata["override"].Should().Be("new");
        provider.LastStreamRequest.Metadata["workflow.run_id"].Should().Be("run-1");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenMetadataOnlyRoutingProvided_ShouldNotPromoteRoutingContext()
    {
        var provider = new StreamingProvider(["A"]);
        var runtime = CreateRuntime(provider);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LLMRequestMetadataKeys.ModelOverride] = "metadata-model",
            [LLMRequestMetadataKeys.NyxIdRoutePreference] = "metadata-route",
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "metadata-token",
        };

        await foreach (var _ in runtime.ChatStreamAsync(
                           "hello",
                           "session-metadata-only",
                           turnCatalog: null,
                           metadata: metadata))
        {
        }

        provider.LastStreamRequest.Should().NotBeNull();
        provider.LastStreamRequest!.RoutingContext.Should().BeNull();
        provider.LastStreamRequest.ToolContext!.Routing.ModelOverride.Should().BeNull();
        provider.LastStreamRequest.ToolContext.Credentials.NyxIdAccessToken.Should().BeNull();
        provider.LastStreamRequest.Metadata.Should().BeEmpty();
    }

    [Fact]
    public async Task ChatStreamAsync_WhenBaseRoutingAndToolRoutingOverlap_ShouldIgnoreToolRoutingForLlmControl()
    {
        var provider = new StreamingProvider(["A"]);
        var runtime = CreateRuntime(
            provider,
            requestBuilder: _ => new LLMRequest
            {
                Messages = [],
                RoutingContext = new LLMRequestRoutingContext(
                    ModelOverride: "base-model",
                    NyxIdRoutePreference: "base-route",
                    MaxToolRoundsOverride: 3,
                    UserMemoryPrompt: "base-memory"),
                ToolContext = AgentToolExecutionContext.Empty with
                {
                    Routing = new LLMRequestRoutingContext(
                        ModelOverride: "typed-model",
                        NyxIdRoutePreference: null,
                        MaxToolRoundsOverride: 9,
                        UserMemoryPrompt: null),
                },
            });

        await foreach (var _ in runtime.ChatStreamAsync("hello", turnCatalog: null))
        {
        }

        provider.LastStreamRequest.Should().NotBeNull();
        provider.LastStreamRequest!.RoutingContext.Should().NotBeNull();
        provider.LastStreamRequest.RoutingContext!.ModelOverride.Should().Be("base-model");
        provider.LastStreamRequest.RoutingContext.NyxIdRoutePreference.Should().Be("base-route");
        provider.LastStreamRequest.RoutingContext.MaxToolRoundsOverride.Should().Be(3);
        provider.LastStreamRequest.RoutingContext.UserMemoryPrompt.Should().Be("base-memory");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenLlmControlProvided_ShouldCarryControlOutsideMetadata()
    {
        var provider = new StreamingProvider(["A"]);
        var runtime = CreateRuntime(provider);
        var control = new LLMControlContext(
            NyxIdAccessToken: "token-1",
            NyxIdOrgToken: "org-1",
            SenderNyxIdAccessToken: null,
            ModelOverride: "control-model",
            NyxIdRoutePreference: "/api/v1/proxy/s/control",
            MaxToolRoundsOverride: 2,
            UserMemoryPrompt: "memory");

        await foreach (var _ in runtime.ChatStreamAsync(
                           [ContentPart.TextPart("hello")],
                           maxToolRounds: 2,
                           requestId: "session-control",
                           llmControl: control,
                           toolContext: null,
                           turnCatalog: null))
        {
        }

        provider.LastStreamRequest.Should().NotBeNull();
        provider.LastStreamRequest!.LlmControl.Should().Be(control);
        provider.LastStreamRequest.Metadata.Should().BeEmpty();
        provider.LastStreamRequest.RoutingContext!.ModelOverride.Should().Be("control-model");
        provider.LastStreamRequest.ToolContext!.Credentials.NyxIdAccessToken.Should().Be("token-1");
    }

    [Fact]
    public async Task ChatStreamAsync_WhenRequestIdentityProvided_ShouldExposeRequestIdToLlmMiddlewareMetadata()
    {
        var provider = new StreamingProvider(["A"]);
        var captureMiddleware = new CaptureLLMMetadataMiddleware();
        var runtime = CreateRuntime(
            provider,
            llmMiddlewares: [captureMiddleware]);

        await foreach (var _ in runtime.ChatStreamAsync("hello", "session-77", turnCatalog: null))
        {
        }

        captureMiddleware.RequestIds.Should().ContainSingle().Which.Should().Be("session-77");
    }

    [Fact]
    public async Task ExplicitTestAggregation_WhenAgentMiddlewareTerminates_ShouldConsumeStreamWithoutCallingProvider()
    {
        var provider = new StreamingProvider(["ignored"]);
        var runtime = CreateRuntime(
            provider,
            agentMiddlewares:
            [
                new DelegateAgentRunMiddleware((context, _) =>
                {
                    context.Result = "short-circuit";
                    context.Terminate = true;
                    return Task.CompletedTask;
                }),
            ]);

        var result = await ChatStreamContentAggregator.AggregateContentAsync(
            runtime.ChatStreamAsync("hello", turnCatalog: null));

        result.Should().Be("short-circuit");
        provider.StreamCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ChatStreamAsync_WhenAgentMiddlewareAwaitsNext_ShouldObserveResultAndItems()
    {
        var provider = new StreamingProvider(["stream-", "answer"]);
        AgentRunContext? observedContext = null;
        var runtime = CreateRuntime(
            provider,
            agentMiddlewares:
            [
                new DelegateAgentRunMiddleware(async (context, next) =>
                {
                    context.Items["agent.before_next"] = "seen";
                    await next();
                    observedContext = context;
                }),
            ]);
        var output = new StringBuilder();

        await foreach (var chunk in runtime.ChatStreamAsync("hello", turnCatalog: null))
        {
            if (!string.IsNullOrEmpty(chunk.DeltaContent))
                output.Append(chunk.DeltaContent);
        }

        output.ToString().Should().Be("stream-answer");
        provider.StreamCallCount.Should().Be(1);
        observedContext.Should().NotBeNull();
        observedContext!.Result.Should().Be("stream-answer");
        observedContext.Items.Should().Contain("agent.before_next", "seen");
        observedContext.Items.Should().Contain("gen_ai.provider.name", "streaming-provider");
    }

    [Fact]
    public async Task ExplicitTestAggregation_WhenProviderStreamsContent_ShouldAggregateStreamContent()
    {
        var provider = new StreamingProvider(["stream-", "answer"]);
        var runtime = CreateRuntime(provider);

        var result = await ChatStreamContentAggregator.AggregateContentAsync(
            runtime.ChatStreamAsync("hello", turnCatalog: null));

        result.Should().Be("stream-answer");
        provider.StreamCallCount.Should().Be(1);
    }

    [Fact]
    public void ChatRuntimePublicSurface_ShouldNotExposeNonStreamingChatAsync()
    {
        var chatAsyncMethods = typeof(ChatRuntime)
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(method => method.Name == "ChatAsync")
            .Select(method => method.ToString())
            .ToArray();

        chatAsyncMethods.Should().BeEmpty();
    }

    [Fact]
    public void UserFacingAiExecutorSurfaces_ShouldNotDirectlyCallProviderChatAsyncOutsideProviderBoundary()
    {
        var root = FindRepositoryRoot();
        var scannedRoots = new[]
        {
            Path.Combine(root, "src", "Aevatar.AI.Core"),
            Path.Combine(root, "src", "Aevatar.Studio.Hosting"),
            Path.Combine(root, "agents", "Aevatar.GAgents.ChatbotClassifier"),
        };
        var offenders = scannedRoots
            .SelectMany(scanRoot => Directory.EnumerateFiles(scanRoot, "*.cs", SearchOption.AllDirectories))
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => new { file, line, index })
                .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(x => x.line.Contains("provider.ChatAsync", StringComparison.Ordinal)
                            || x.line.Contains("_provider.ChatAsync", StringComparison.Ordinal))
                .Select(x => $"{Path.GetRelativePath(root, x.file)}:{x.index + 1}:{x.line.Trim()}"))
            .ToArray();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void ProviderContractSurfaces_ShouldNotDeclareNonStreamingChatAsync()
    {
        var root = FindRepositoryRoot();
        var providerContractFile = Path.Combine(
            root,
            "src",
            "Aevatar.AI.Abstractions",
            "LLMProviders",
            "ILLMProvider.cs");
        var concreteProviderRoots = new[]
        {
            Path.Combine(root, "src", "Aevatar.AI.Core", "LLMProviders"),
            Path.Combine(root, "src", "Aevatar.AI.LLMProviders.MEAI"),
            Path.Combine(root, "src", "Aevatar.AI.LLMProviders.NyxId"),
            Path.Combine(root, "src", "Aevatar.AI.LLMProviders.Tornado"),
        };

        var scannedFiles = new[] { providerContractFile }
            .Concat(concreteProviderRoots.SelectMany(scanRoot =>
                Directory.EnumerateFiles(scanRoot, "*.cs", SearchOption.AllDirectories)));
        var offenders = scannedFiles
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => new { file, line, index })
                .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(x => System.Text.RegularExpressions.Regex.IsMatch(
                    x.line,
                    @"Task<LLMResponse>\s+ChatAsync\s*\("))
                .Select(x => $"{Path.GetRelativePath(root, x.file)}:{x.index + 1}:{x.line.Trim()}"))
            .ToArray();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public async Task ChatStreamAsync_WhenAgentMiddlewareTerminates_ShouldEmitSyntheticContentChunk()
    {
        var provider = new StreamingProvider(["ignored"]);
        var runtime = CreateRuntime(
            provider,
            agentMiddlewares:
            [
                new DelegateAgentRunMiddleware((context, _) =>
                {
                    context.Result = "agent-short-circuit";
                    context.Terminate = true;
                    return Task.CompletedTask;
                }),
            ]);
        var chunks = new List<LLMStreamChunk>();

        await foreach (var chunk in runtime.ChatStreamAsync("hello", turnCatalog: null))
            chunks.Add(chunk);

        chunks.Should().ContainSingle();
        chunks[0].DeltaContent.Should().Be("agent-short-circuit");
        provider.StreamCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ChatStreamAsync_WhenLlmMiddlewareTerminates_ShouldEmitSyntheticContentAndToolCallChunks()
    {
        var provider = new StreamingProvider(["ignored"]);
        var runtime = CreateRuntime(
            provider,
            llmMiddlewares:
            [
                new DelegateLlmCallMiddleware((context, _) =>
                {
                    context.Terminate = true;
                    context.Response = new LLMResponse
                    {
                        Content = "middleware-content",
                        ToolCalls =
                        [
                            new ToolCall
                            {
                                Id = "tool-1",
                                Name = "search",
                                ArgumentsJson = "{\"q\":\"aevatar\"}",
                            },
                        ],
                    };
                    return Task.CompletedTask;
                }),
            ]);
        var chunks = new List<LLMStreamChunk>();

        await foreach (var chunk in runtime.ChatStreamAsync("hello", turnCatalog: null))
            chunks.Add(chunk);

        chunks.Should().Contain(x => x.DeltaContent == "middleware-content");
        chunks.Should().Contain(x => x.DeltaToolCall != null && x.DeltaToolCall.Id == "tool-1");
        chunks.Should().Contain(x => x.IsLast);
        provider.StreamCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ChatStreamAsync_WhenLlmMiddlewareTerminates_ShouldEmitReasoningContentChunk()
    {
        var provider = new StreamingProvider(["ignored"]);
        var runtime = CreateRuntime(
            provider,
            llmMiddlewares:
            [
                new DelegateLlmCallMiddleware((context, _) =>
                {
                    context.Terminate = true;
                    context.Response = new LLMResponse
                    {
                        Content = "answer",
                        ReasoningContent = "thinking-step",
                    };
                    return Task.CompletedTask;
                }),
            ]);
        var chunks = new List<LLMStreamChunk>();

        await foreach (var chunk in runtime.ChatStreamAsync("hello", turnCatalog: null))
            chunks.Add(chunk);

        chunks.Should().Contain(x => x.DeltaReasoningContent == "thinking-step");
        chunks.Should().Contain(x => x.DeltaContent == "answer");
        chunks.Should().Contain(x => x.IsLast);
        provider.StreamCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ChatStreamAsync_WhenProviderEmitsEmptyNonTerminalChunk_ShouldFilterItOut()
    {
        var provider = new StreamingProvider(
            chunks: [],
            streamToolDeltas:
            [
                new LLMStreamChunk(),
            ]);
        var runtime = CreateRuntime(provider);
        var chunks = new List<LLMStreamChunk>();

        await foreach (var chunk in runtime.ChatStreamAsync("hello", turnCatalog: null))
            chunks.Add(chunk);

        chunks.Should().BeEmpty();
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

    private static ChatRuntime CreateRuntime(
        ILLMProvider provider,
        ToolManager? tools = null,
        IReadOnlyList<IAgentRunMiddleware>? agentMiddlewares = null,
        IReadOnlyList<ILLMCallMiddleware>? llmMiddlewares = null,
        Func<AgentProfileTurnCatalog?, LLMRequest>? requestBuilder = null)
    {
        var history = new ChatHistory();
        var effectiveTools = tools ?? new ToolManager();
        var toolLoop = new ToolCallLoop(
            effectiveTools,
            toolExecutionPort: new TestAgentToolExecutionPort());

        return new ChatRuntime(
            providerFactory: () => provider,
            history: history,
            toolLoop: toolLoop,
            hooks: null,
            requestBuilder: requestBuilder ?? (_ => new LLMRequest
            {
                Messages = [],
                Tools = effectiveTools.GetAll(),
            }),
            agentMiddlewares: agentMiddlewares,
            llmMiddlewares: llmMiddlewares);
    }

    private static string StripLineComments(string source)
    {
        var lines = source
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
        return string.Join('\n', lines);
    }

    private sealed class QueuedStreamingProvider(
        IReadOnlyList<IReadOnlyList<LLMStreamChunk>> rounds) : ILLMProvider
    {
        private readonly Queue<IReadOnlyList<LLMStreamChunk>> _rounds = new(rounds);

        public string Name => "queued-streaming-provider";
        public List<LLMRequest> StreamRequests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            StreamRequests.Add(request);
            var round = _rounds.Count > 0 ? _rounds.Dequeue() : [];

            foreach (var chunk in round)
            {
                ct.ThrowIfCancellationRequested();
                yield return chunk;
                await Task.Yield();
            }
        }
    }

    private sealed class RecordingStepProvider : ILLMProvider
    {
        public string Name => "recording-step";
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            yield return new LLMStreamChunk { DeltaContent = "answer" };
            await Task.Yield();
            yield return new LLMStreamChunk { IsLast = true, FinishReason = "stop" };
        }
    }

    private sealed class CapturingTool : IAgentTool
    {
        public string Name => "capture";
        public string Description => "capture context";
        public string ParametersSchema => "{}";
        public AgentToolExecutionContext? CapturedContext { get; private set; }
        public AgentToolReceipt? CreateSuccessReceipt(
            string callId,
            string toolName,
            string resultJson) => SuccessReceipt(callId, toolName, resultJson);

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            CapturedContext = AgentToolRequestContext.Current;
            return Task.FromResult("""{"result":"tool-ok"}""");
        }
    }

    private sealed class StreamingProvider(
        IReadOnlyList<string> chunks,
        ToolCall? streamToolCall = null,
        IReadOnlyList<LLMStreamChunk>? streamToolDeltas = null) : ILLMProvider
    {
        public string Name => "streaming-provider";
        public int StreamCallCount { get; private set; }
        public LLMRequest? LastStreamRequest { get; private set; }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastStreamRequest = request;
            StreamCallCount++;

            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return new LLMStreamChunk { DeltaContent = chunk };
                await Task.Yield();
            }

            if (streamToolDeltas is { Count: > 0 })
            {
                foreach (var streamChunk in streamToolDeltas)
                {
                    yield return streamChunk;
                    await Task.Yield();
                }
            }
            else if (streamToolCall != null)
            {
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = streamToolCall,
                };
            }
        }
    }

    private sealed class CaptureLLMResponseMiddleware : ILLMCallMiddleware
    {
        public LLMResponse? LastResponse { get; private set; }

        public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
        {
            await next();
            LastResponse = context.Response;
        }
    }

    private sealed class CaptureLLMMetadataMiddleware : ILLMCallMiddleware
    {
        public List<string> RequestIds { get; } = [];

        public async Task InvokeAsync(LLMCallContext context, Func<Task> next)
        {
            if (context.Items.TryGetValue(LLMRequestMetadataKeys.RequestId, out var requestIdObj) &&
                requestIdObj is string requestId)
            {
                RequestIds.Add(requestId);
            }

            await next();
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "aevatar.slnx")))
                return current;

            current = Directory.GetParent(current)?.FullName;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class DelegateAgentRunMiddleware(
        Func<AgentRunContext, Func<Task>, Task> handler) : IAgentRunMiddleware
    {
        public Task InvokeAsync(AgentRunContext context, Func<Task> next) => handler(context, next);
    }

    private sealed class DelegateLlmCallMiddleware(
        Func<LLMCallContext, Func<Task>, Task> handler) : ILLMCallMiddleware
    {
        public Task InvokeAsync(LLMCallContext context, Func<Task> next) => handler(context, next);
    }

    private sealed class DelegateTool(
        string name,
        Func<string, string> execute,
        bool isReadOnly = false,
        bool isDestructive = false,
        string sideEffectKind = "") : IAgentTool
    {
        public string Name => name;
        public string Description => "delegate";
        public string ParametersSchema => "{}";
        public bool IsReadOnly => isReadOnly;
        public bool IsDestructive => isDestructive;
        public string SideEffectKind => sideEffectKind;
        public AgentToolReceipt? CreateSuccessReceipt(
            string callId,
            string toolName,
            string resultJson) => SuccessReceipt(callId, toolName, resultJson);

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(execute(argumentsJson));
        }
    }

    private sealed class TestAgentToolExecutionPort : IAgentToolExecutionPort
    {
        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            var safety = request.Tool.GetCallSafety(request.ArgumentsJson);
            try
            {
                var resultJson = await request.Tool.ExecuteAsync(request.ArgumentsJson, ct);
                return new AgentToolExecutionOutcome(
                    AgentToolExecutionOutcomeKind.Executed,
                    resultJson,
                    AgentToolReceiptFactory.CreateResult(
                        request.Tool,
                        request.ExecutionContext.Request.CallId ?? string.Empty,
                        request.Tool.Name,
                        safety,
                        resultJson,
                        request.ArgumentsJson),
                    IsMutation: !safety.IsReadOnly,
                    FailureCode: string.Empty,
                    SafeMessage: string.Empty,
                    AgentToolExecutionFailureStage.None,
                    TerminalInvoked: true,
                    Retryable: false,
                    AuditCompleted: true);
            }
            catch (Exception ex)
            {
                var resultJson = ToolManager.BuildErrorJson("The tool request failed.");
                return new AgentToolExecutionOutcome(
                    AgentToolExecutionOutcomeKind.Failed,
                    resultJson,
                    AgentToolReceiptFactory.CreateError(
                        request.Tool,
                        request.ExecutionContext.Request.CallId ?? string.Empty,
                        request.Tool.Name,
                        safety,
                        resultJson,
                        "tool_execution_exception",
                        ex.GetType().Name),
                    IsMutation: !safety.IsReadOnly,
                    FailureCode: "tool_execution_exception",
                    SafeMessage: ex.GetType().Name,
                    AgentToolExecutionFailureStage.TerminalExecution,
                    TerminalInvoked: true,
                    Retryable: false,
                    AuditCompleted: true);
            }
        }
    }

    private static AgentToolReceipt SuccessReceipt(
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

    private sealed class ReceiptTool(string name, AgentToolReceiptStatus status) : IAgentTool
    {
        public string Name => name;
        public string Description => "returns a typed failed receipt";
        public string ParametersSchema => "{}";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("{\"error\":\"unsafe tool-secret\"}");
        }

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            new()
            {
                CallId = callId,
                ToolName = toolName,
                Status = status,
                ErrorCode = "SAFE_TOOL_FAILURE",
                ErrorMessage = "The tool request failed.",
                ResultJson = "{\"error\":\"safe tool failure\"}",
            };
    }
}
