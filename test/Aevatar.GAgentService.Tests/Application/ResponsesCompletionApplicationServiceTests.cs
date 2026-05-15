using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ResponsesCompletionApplicationServiceTests
{
    private static readonly IReadOnlyDictionary<string, string> ToolContext =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scope_id"] = "scope-1",
            ["owner_subject"] = "owner-1",
        };

    private static readonly ResponsesToolProviderContext ToolProviderContext = new(
        new ResponsesToolProviderCallerScope("scope-1", "owner-1", "ApiKey"),
        ToolContext);

    [Fact]
    public async Task CollectAsync_ShouldExecuteLocalTool_AndContinueWithToolResult()
    {
        var tool = new RecordingTool("local_tool", """{"type":"object"}""", """{"ok":true}""");
        var provider = new RecordingLlmProvider((round, _) => round == 1
            ? [
                new LLMStreamChunk { DeltaContent = "before ", Usage = new TokenUsage(1, 2, 3) },
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "local_tool",
                        ArgumentsJson = """{"query":"weather"}""",
                    },
                    IsLast = true,
                },
            ]
            : [
                new LLMStreamChunk
                {
                    DeltaContentPart = ContentPart.TextPart("after"),
                    Usage = new TokenUsage(4, 5, 9),
                    IsLast = true,
                },
            ]);
        var request = BuildRequest(tool);
        var previous = AgentToolRequestContext.CurrentMetadata;

        try
        {
            AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
            {
                ["outer"] = "preserved",
            };

            var result = await new ResponsesCompletionApplicationService().CollectAsync(
                provider,
                request,
                ToolContext,
                new ResponsesToolClassification([], [tool], [], []));

            result.Text.Should().Be("before after");
            result.Usage.Should().Be(new TokenUsage(4, 5, 9));
            result.ForwardedToolCalls.Should().BeEmpty();
            provider.Requests.Should().HaveCount(2);
            provider.Requests[1].CallerContext.Should().Be(request.CallerContext);
            provider.Requests[1].Metadata.Should().BeSameAs(request.Metadata);
            provider.Requests[1].Messages.Should().ContainSingle(message =>
                message.Role == "assistant" && message.ToolCalls != null && message.ToolCalls[0].Name == "local_tool");
            provider.Requests[1].Messages.Should().ContainSingle(message =>
                message.Role == "tool" && message.ToolCallId == "call_1" && message.Content == """{"ok":true}""");
            tool.LastArgumentsJson.Should().Be("""{"query":"weather"}""");
            tool.LastMetadata.Should().Contain("scope_id", "scope-1");
            AgentToolRequestContext.CurrentMetadata.Should().Contain("outer", "preserved");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = previous;
        }
    }

    [Fact]
    public async Task CollectAsync_ShouldReturnForwardedToolCall_WithPromotedStreamingId()
    {
        var forwarded = new ResponsesApplicationToolDeclaration(
            "client_tool",
            "Client owned tool",
            """{"type":"object"}""",
            "client-schema");
        var provider = new RecordingLlmProvider((_, _) =>
        [
            new LLMStreamChunk { DeltaContent = "need client " },
            new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = string.Empty,
                    Name = "client_tool",
                    ArgumentsJson = """{"city":""",
                },
            },
            new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "call_client",
                    Name = string.Empty,
                    ArgumentsJson = "\"Singapore\"}",
                },
                Usage = new TokenUsage(3, 4, 7),
                IsLast = true,
            },
        ]);

        var result = await new ResponsesCompletionApplicationService().CollectAsync(
            provider,
            BuildRequest(),
            ToolContext,
            new ResponsesToolClassification([forwarded], [], [], []));

        result.Text.Should().Be("need client ");
        result.Usage.Should().Be(new TokenUsage(3, 4, 7));
        result.ForwardedToolCalls.Should().ContainSingle().Which.Should().BeEquivalentTo(new ToolCall
        {
            Id = "call_client",
            Name = "client_tool",
            ArgumentsJson = """{"city":"Singapore"}""",
        });
        provider.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task CollectAsync_ShouldAppendMissingToolError_WhenToolIsNotRegisteredOnRequest()
    {
        var advertisedTool = new RecordingTool("missing_tool", """{"type":"object"}""", "{}");
        var requestTool = new RecordingTool("other_tool", """{"type":"object"}""", "{}");
        var provider = new RecordingLlmProvider((round, request) =>
        {
            if (round == 1)
            {
                return
                [
                    new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = "call_missing",
                            Name = "missing_tool",
                            ArgumentsJson = "   ",
                        },
                        IsLast = true,
                    },
                ];
            }

            request.Messages.Should().ContainSingle(message =>
                message.Role == "tool" &&
                message.ToolCallId == "call_missing" &&
                message.Content!.Contains("aevatar_substitute_tool_not_registered", StringComparison.Ordinal));
            return [new LLMStreamChunk { DeltaContent = "fallback", IsLast = true }];
        });

        var result = await new ResponsesCompletionApplicationService().CollectAsync(
            provider,
            BuildRequest(requestTool),
            ToolContext,
            new ResponsesToolClassification([], [advertisedTool], [], []));

        result.Text.Should().Be("fallback");
        requestTool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task CollectAsync_ShouldStopAfterBoundedLocalToolRounds()
    {
        var tool = new RecordingTool("local_tool", """{"type":"object"}""", "{}");
        var provider = new RecordingLlmProvider((_, _) =>
        [
            new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "local_tool",
                    ArgumentsJson = "{}",
                },
                IsLast = true,
            },
        ]);

        var result = await new ResponsesCompletionApplicationService().CollectAsync(
            provider,
            BuildRequest(tool),
            ToolContext,
            new ResponsesToolClassification([], [tool], [], []));

        result.Text.Should().BeEmpty();
        result.ForwardedToolCalls.Should().BeEmpty();
        provider.Requests.Should().HaveCount(8);
        tool.ExecuteCount.Should().Be(8);
    }

    [Fact]
    public async Task CollectAsync_ShouldExecuteLocalTool_WhenForwardedToolsAreAlsoDeclared()
    {
        var localTool = new RecordingTool("local_tool", """{"type":"object"}""", """{"done":true}""");
        var forwarded = new ResponsesApplicationToolDeclaration(
            "client_tool",
            "client tool",
            """{"type":"object"}""",
            "client-hash");
        var provider = new RecordingLlmProvider((round, _) => round == 1
            ? [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_local",
                        Name = "local_tool",
                        ArgumentsJson = "{}",
                    },
                    IsLast = true,
                },
            ]
            : [
                new LLMStreamChunk { DeltaContent = "done", IsLast = true },
            ]);

        var result = await new ResponsesCompletionApplicationService().CollectAsync(
            provider,
            BuildRequest(localTool),
            ToolContext,
            new ResponsesToolClassification(
                [forwarded],
                [localTool, new ResponsesForwardedTool(forwarded)],
                [],
                []));

        result.Text.Should().Be("done");
        result.ForwardedToolCalls.Should().BeEmpty();
        localTool.ExecuteCount.Should().Be(1);
    }

    [Fact]
    public async Task CollectAsync_ShouldContinueWithoutToolResult_WhenRequestHasNoRegisteredTools()
    {
        var advertisedTool = new RecordingTool("local_tool", """{"type":"object"}""", "{}");
        var provider = new RecordingLlmProvider((round, request) =>
        {
            if (round == 1)
            {
                return
                [
                    new LLMStreamChunk
                    {
                        DeltaToolCall = new ToolCall
                        {
                            Id = "call_local",
                            Name = "local_tool",
                            ArgumentsJson = "{}",
                        },
                        IsLast = true,
                    },
                ];
            }

            request.Messages.Should().NotContain(message => message.Role == "tool");
            return [new LLMStreamChunk { DeltaContent = "done", IsLast = true }];
        });

        var result = await new ResponsesCompletionApplicationService().CollectAsync(
            provider,
            BuildRequest(),
            ToolContext,
            new ResponsesToolClassification([], [advertisedTool], [], []));

        result.Text.Should().Be("done");
        advertisedTool.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task StreamAsync_ShouldEmitTextDeltas_ExecuteLocalTool_AndRestoreMetadata()
    {
        var tool = new RecordingTool("local_tool", """{"type":"object"}""", """{"done":true}""");
        var provider = new RecordingLlmProvider((round, _) => round == 1
            ? [
                new LLMStreamChunk { DeltaContent = "stream " },
                new LLMStreamChunk { DeltaContentPart = ContentPart.TextPart("part ") },
                new LLMStreamChunk { DeltaContent = "   " },
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "local_tool",
                        ArgumentsJson = string.Empty,
                    },
                    IsLast = true,
                },
            ]
            : [
                new LLMStreamChunk
                {
                    DeltaContent = "done",
                    Usage = new TokenUsage(10, 11, 21),
                    IsLast = true,
                },
            ]);
        var deltas = new List<string>();
        var previous = AgentToolRequestContext.CurrentMetadata;

        try
        {
            AgentToolRequestContext.CurrentMetadata = new Dictionary<string, string>
            {
                ["outer"] = "stream",
            };

            var result = await new ResponsesCompletionApplicationService().StreamAsync(
                provider,
                BuildRequest(tool),
                ToolContext,
                new ResponsesToolClassification([], [tool], [], []),
                (delta, _) =>
                {
                    deltas.Add(delta);
                    return ValueTask.CompletedTask;
                });

            result.Text.Should().Be("stream part done");
            result.Usage.Should().Be(new TokenUsage(10, 11, 21));
            deltas.Should().Equal("stream ", "part ", "done");
            tool.LastArgumentsJson.Should().Be("{}");
            AgentToolRequestContext.CurrentMetadata.Should().Contain("outer", "stream");
        }
        finally
        {
            AgentToolRequestContext.CurrentMetadata = previous;
        }
    }

    [Fact]
    public async Task StreamAsync_ShouldReturnForwardedToolCall_WithoutExecutingLocally()
    {
        var forwarded = new ResponsesApplicationToolDeclaration(
            "client_tool",
            "client tool",
            """{"type":"object"}""",
            "client-hash");
        var provider = new RecordingLlmProvider((_, _) =>
        [
            new LLMStreamChunk { DeltaContent = "client " },
            new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "call_client",
                    Name = "client_tool",
                    ArgumentsJson = """{"ok":true}""",
                },
                Usage = new TokenUsage(2, 3, 5),
                IsLast = true,
            },
        ]);
        var deltas = new List<string>();

        var result = await new ResponsesCompletionApplicationService().StreamAsync(
            provider,
            BuildRequest(),
            ToolContext,
            new ResponsesToolClassification([forwarded], [new ResponsesForwardedTool(forwarded)], [], []),
            (delta, _) =>
            {
                deltas.Add(delta);
                return ValueTask.CompletedTask;
            });

        result.Text.Should().Be("client ");
        result.Usage.Should().Be(new TokenUsage(2, 3, 5));
        result.ForwardedToolCalls.Should().ContainSingle().Which.Should().BeEquivalentTo(new ToolCall
        {
            Id = "call_client",
            Name = "client_tool",
            ArgumentsJson = """{"ok":true}""",
        });
        deltas.Should().ContainSingle("client ");
    }

    [Fact]
    public async Task StreamAsync_ShouldStopAfterBoundedLocalToolRounds()
    {
        var tool = new RecordingTool("local_tool", """{"type":"object"}""", "{}");
        var provider = new RecordingLlmProvider((_, _) =>
        [
            new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "local_tool",
                    ArgumentsJson = "{}",
                },
                IsLast = true,
            },
        ]);

        var result = await new ResponsesCompletionApplicationService().StreamAsync(
            provider,
            BuildRequest(tool),
            ToolContext,
            new ResponsesToolClassification([], [tool], [], []),
            (_, _) => ValueTask.CompletedTask);

        result.Text.Should().BeEmpty();
        result.ForwardedToolCalls.Should().BeEmpty();
        provider.Requests.Should().HaveCount(8);
        tool.ExecuteCount.Should().Be(8);
    }

    [Fact]
    public async Task ClassifyAsync_ShouldSubstituteForwardAndDeduplicateAdditiveTools()
    {
        var substitute = new RecordingTool("web_search", """{"type":"object","properties":{"q":{"type":"string"}}}""", "{}");
        var duplicateSubstitute = new RecordingTool("web_search", """{"type":"object"}""", "{}");
        var additive = new RecordingTool("aevatar_todo_write", """{"type":"object"}""", "{}");
        var duplicateAdditive = new RecordingTool("aevatar_todo_write", """{"type":"object"}""", "{}");
        var customAdditive = new RecordingTool("custom_additive", """{"type":"object"}""", "{}");
        var logger = new RecordingLogger();

        var result = await ResponsesToolClassifier.ClassifyAsync(
            [
                new ResponsesApplicationToolDeclaration("web_search", "client search", """{"type":"object"}""", "mismatch"),
                new ResponsesApplicationToolDeclaration("client_tool", "client tool", """{"type":"object"}""", "client-hash"),
            ],
            [
                new RecordingResponsesToolProvider(
                    [substitute, duplicateSubstitute],
                    [additive, duplicateAdditive, customAdditive]),
            ],
            ToolProviderContext,
            logger);

        result.ForwardedTools.Should().ContainSingle(x => x.Name == "client_tool");
        result.SubstitutedToolNames.Should().ContainSingle("web_search");
        result.AdditiveToolNames.Should().Equal("aevatar_todo_write", "custom_additive");
        result.EffectiveTools.Select(static tool => tool.Name)
            .Should().Equal("web_search", "client_tool", "aevatar_todo_write", "custom_additive");
        logger.Messages.Should().Contain(message =>
            message.Contains("schema differs", StringComparison.Ordinal));
        result.EffectiveTools.Single(tool => tool.Name == "client_tool").IsReadOnly.Should().BeTrue();
        await ((Func<Task>)(() => result.EffectiveTools.Single(tool => tool.Name == "client_tool").ExecuteAsync("{}")))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must be executed by the client*");
    }

    [Fact]
    public async Task ClassifyAsync_ShouldSkipAdditiveToolsThatCollideWithEffectiveTools()
    {
        var logger = new RecordingLogger();

        var result = await ResponsesToolClassifier.ClassifyAsync(
            [
                new ResponsesApplicationToolDeclaration("use_skill", "client tool", """{"type":"object"}""", "client-hash"),
            ],
            [
                new RecordingResponsesToolProvider(
                    [],
                    [
                        new RecordingTool("use_skill", """{"type":"object"}""", "{}"),
                        new RecordingTool("ornn_search_skills", """{"type":"object"}""", "{}"),
                    ]),
            ],
            ToolProviderContext,
            logger);

        result.ForwardedTools.Should().ContainSingle(x => x.Name == "use_skill");
        result.EffectiveTools.Select(static tool => tool.Name)
            .Should().Equal("use_skill", "ornn_search_skills");
        result.AdditiveToolNames.Should().ContainSingle("ornn_search_skills");
        logger.Messages.Should().ContainSingle(message =>
            message.Contains("skipped", StringComparison.Ordinal) &&
            message.Contains("use_skill", StringComparison.Ordinal));
    }

    [Fact]
    public void ResponsesForwardedTool_ShouldExposeDeclarationAndRejectNull()
    {
        ((Action)(() => new ResponsesForwardedTool(null!)))
            .Should().Throw<ArgumentNullException>();

        var declaration = new ResponsesApplicationToolDeclaration(
            "client_tool",
            "client description",
            """{"type":"object"}""",
            "schema-hash");

        var tool = new ResponsesForwardedTool(declaration);

        tool.Name.Should().Be("client_tool");
        tool.Description.Should().Be("client description");
        tool.ParametersSchema.Should().Be("""{"type":"object"}""");
        tool.SchemaHash.Should().Be("schema-hash");
        tool.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public async Task ResponsesToolProvider_DefaultMethods_ShouldReturnEmptyLists()
    {
        IResponsesToolProvider provider = new EmptyResponsesToolProvider();

        (await provider.GetSubstituteToolsAsync(ToolProviderContext)).Should().BeEmpty();
        (await provider.GetAdditiveToolsAsync(ToolProviderContext)).Should().BeEmpty();
    }

    [Fact]
    public void ResponsesToolCallAccumulator_ShouldAppendRepeatedAnonymousDeltas()
    {
        var accumulator = new ResponsesToolCallAccumulator();

        accumulator.TrackDelta(new ToolCall
        {
            Id = string.Empty,
            Name = "client_tool",
            ArgumentsJson = """{"city":""",
        });
        accumulator.TrackDelta(new ToolCall
        {
            Id = string.Empty,
            Name = string.Empty,
            ArgumentsJson = "\"Singapore\"}",
        });

        accumulator.BuildToolCalls().Should().ContainSingle().Which.Should().BeEquivalentTo(new ToolCall
        {
            Id = "stream-tool-call-1",
            Name = "client_tool",
            ArgumentsJson = """{"city":"Singapore"}""",
        });
    }

    [Fact]
    public void ResponsesToolCallAccumulator_ShouldKeepAnonymousAggregate_WhenKnownIdAlreadyExists()
    {
        var accumulator = new ResponsesToolCallAccumulator();

        accumulator.TrackDelta(new ToolCall
        {
            Id = "call_existing",
            Name = "client_tool",
            ArgumentsJson = """{"known":""",
        });
        accumulator.TrackDelta(new ToolCall
        {
            Id = string.Empty,
            Name = "client_tool",
            ArgumentsJson = """{"anonymous":true}""",
        });
        accumulator.TrackDelta(new ToolCall
        {
            Id = "call_existing",
            Name = string.Empty,
            ArgumentsJson = "\"done\"}",
        });

        accumulator.BuildToolCalls().Should().BeEquivalentTo(
            [
                new ToolCall
                {
                    Id = "call_existing",
                    Name = "client_tool",
                    ArgumentsJson = """{"known":"done"}""",
                },
                new ToolCall
                {
                    Id = "stream-tool-call-1",
                    Name = "client_tool",
                    ArgumentsJson = """{"anonymous":true}""",
                },
            ],
            options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task PublicMethods_ShouldRejectNullArguments()
    {
        var service = new ResponsesCompletionApplicationService();
        var provider = new RecordingLlmProvider((_, _) => []);
        var request = BuildRequest();
        var classification = new ResponsesToolClassification([], [], [], []);

        await ((Func<Task>)(() => service.CollectAsync(null!, request, ToolContext, classification)))
            .Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(() => service.CollectAsync(provider, null!, ToolContext, classification)))
            .Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(() => service.CollectAsync(provider, request, null!, classification)))
            .Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(() => service.CollectAsync(provider, request, ToolContext, null!)))
            .Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(() => service.StreamAsync(provider, request, ToolContext, classification, null!)))
            .Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(async () => await ResponsesToolClassifier.ClassifyAsync(null!, [], ToolProviderContext, new RecordingLogger())))
            .Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(async () => await ResponsesToolClassifier.ClassifyAsync([], null!, ToolProviderContext, new RecordingLogger())))
            .Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(async () => await ResponsesToolClassifier.ClassifyAsync([], [], null!, new RecordingLogger())))
            .Should().ThrowAsync<ArgumentNullException>();
        await ((Func<Task>)(async () => await ResponsesToolClassifier.ClassifyAsync([], [], ToolProviderContext, null!)))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    private static LLMRequest BuildRequest(params IAgentTool[] tools) =>
        new()
        {
            Messages = [ChatMessage.User("hello")],
            RequestId = "request-1",
            Metadata = new Dictionary<string, string> { ["request"] = "metadata" },
            CallerContext = new LLMRequestCallerContext("scope-1", "owner-1", "resp_1"),
            Tools = tools,
            Model = "test-model",
            Temperature = 0.25,
            MaxTokens = 128,
        };

    private sealed class RecordingLlmProvider(
        Func<int, LLMRequest, IReadOnlyList<LLMStreamChunk>> chunksForRound) : ILLMProvider
    {
        public string Name => "recording";

        public List<LLMRequest> Requests { get; } = [];

        public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            foreach (var chunk in chunksForRound(Requests.Count, request))
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return chunk;
            }
        }
    }

    private sealed class RecordingTool(
        string name,
        string parametersSchema,
        string resultJson) : IAgentTool
    {
        public string Name { get; } = name;

        public string Description => $"{Name} description";

        public string ParametersSchema { get; } = parametersSchema;

        public bool IsReadOnly => true;

        public string? LastArgumentsJson { get; private set; }

        public IReadOnlyDictionary<string, string>? LastMetadata { get; private set; }

        public int ExecuteCount { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCount++;
            LastArgumentsJson = argumentsJson;
            LastMetadata = AgentToolRequestContext.CurrentMetadata;
            return Task.FromResult(resultJson);
        }
    }

    private sealed class RecordingResponsesToolProvider(
        IReadOnlyList<IAgentTool> substituteTools,
        IReadOnlyList<IAgentTool> additiveTools) : IResponsesToolProvider
    {
        public ValueTask<IReadOnlyList<IAgentTool>> GetSubstituteToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default) =>
            ValueTask.FromResult(substituteTools);

        public ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default) =>
            ValueTask.FromResult(additiveTools);
    }

    private sealed class EmptyResponsesToolProvider : IResponsesToolProvider
    {
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
