using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Foundation.Abstractions.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class ChatRuntimeToolProgressTests
{
    [Fact]
    public async Task ChatStreamAsync_InitialSkillRecovery_ShouldYieldStartBeforeExecutionAndResult()
    {
        var provider = new AnswerProvider();
        var tool = new ControlledRecoveryTool();
        var tools = new ToolManager();
        tools.Register(tool);
        var runtime = new ChatRuntime(
            providerFactory: () => provider,
            history: new ChatHistory(),
            toolLoop: CreateToolCallLoop(tools),
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages = [],
                Tools = tools.GetAll(),
                ToolContext = TestToolContext,
            });
        var toolContext = AgentToolExecutionContext.Empty with
        {
            ExecutionOwner = AgentToolExecutionOwners.HostService(nameof(ChatRuntimeToolProgressTests)),
            SkillRecovery = new AgentSkillRecoveryContext(
                RequireInitialOrnnSearch: true,
                RequireOrnnSearchOnBlocker: false,
                CommandName: "goal",
                OriginalCommand: "/goal ship",
                PrimarySkillName: "project-summary",
                MaxOrnnSearchAttempts: 1,
                CommandArguments: "ship"),
        };

        await using var stream = runtime.ChatStreamAsync(
                [ContentPart.TextPart("/goal ship")],
                maxToolRounds: 1,
                requestId: "recovery-request",
                toolContext,
                turnCatalog: null,
                metadata: null)
            .GetAsyncEnumerator();
        var firstMove = stream.MoveNextAsync().AsTask();

        try
        {
            var firstSignal = await Task.WhenAny(firstMove, tool.Started.Task);
            firstSignal.Should().BeSameAs(firstMove,
                "the recovery tool must not execute until its start carrier is consumed");
            (await firstMove).Should().BeTrue();
            stream.Current.ToolCallStarted.Should().NotBeNull();
            stream.Current.ToolCallStarted!.ToolCall.Name.Should().Be("ornn_search_skills");
            stream.Current.ToolCallStarted.Presentation.Kind.Should().Be(ToolPresentationKind.Generic);
            tool.Started.Task.IsCompleted.Should().BeFalse();

            var resume = stream.MoveNextAsync().AsTask();
            var executionSignal = await Task.WhenAny(tool.Started.Task, resume);
            executionSignal.Should().BeSameAs(tool.Started.Task,
                "the admitted terminal must invoke the recovery tool before publishing its result");
            resume.IsCompleted.Should().BeFalse();
            tool.Release("{\"status\":\"no_match\",\"matches\":[]}");
            (await resume).Should().BeTrue();
            stream.Current.ToolCallCompleted.Should().NotBeNull();
            stream.Current.ToolCallCompleted!.ToolName.Should().Be("ornn_search_skills");
            stream.Current.ToolCallCompleted.ResultJson.Should().Be("{\"status\":\"no_match\",\"matches\":[]}");
        }
        finally
        {
            tool.Release("{\"status\":\"no_match\",\"matches\":[]}");
            if (!firstMove.IsCompleted)
                await firstMove;
        }
    }

    [Fact]
    public async Task ChatStreamAsync_ShouldYieldToolStartBeforeExecutionAndEveryToolResult()
    {
        var provider = new ToolThenAnswerProvider();
        var tool = new ControlledTool();
        var tools = new ToolManager();
        tools.Register(tool);
        var runtime = new ChatRuntime(
            providerFactory: () => provider,
            history: new ChatHistory(),
            toolLoop: CreateToolCallLoop(tools),
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages = [],
                Tools = tools.GetAll(),
                ToolContext = TestToolContext,
            });

        await using var stream = runtime.ChatStreamAsync(
                "hello",
                maxToolRounds: 2,
                requestId: "tool-progress-request",
                turnCatalog: null)
            .GetAsyncEnumerator();
        LLMStreamChunk? started = null;
        while (await stream.MoveNextAsync())
        {
            if (stream.Current.ToolCallStarted != null)
            {
                started = stream.Current;
                break;
            }
        }

        started.Should().NotBeNull();
        started!.ToolCallStarted!.ToolCall.Id.Should().Be("call-1");
        started.ToolCallStarted.Presentation.InvocationName.Should().Be("controlled_tool");
        started.ToolCallStarted.Presentation.Kind.Should().Be(ToolPresentationKind.Generic);
        tool.Started.Task.IsCompleted.Should().BeFalse(
            "the caller must commit TOOL_CALL_START before advancing the iterator into execution");

        var resume = stream.MoveNextAsync().AsTask();
        var executionSignal = await Task.WhenAny(tool.Started.Task, resume);
        executionSignal.Should().BeSameAs(tool.Started.Task,
            "the admitted terminal must invoke the tool before publishing its result");
        resume.IsCompleted.Should().BeFalse("the stream is waiting for controlled tool completion");
        tool.Release("{\"ok\":true}");
        (await resume).Should().BeTrue();

        ToolCallCompletedChunk? completed = stream.Current.ToolCallCompleted;
        while (completed == null && await stream.MoveNextAsync())
        {
            if (stream.Current.ToolCallCompleted != null)
            {
                completed = stream.Current.ToolCallCompleted;
                break;
            }
        }

        completed.Should().NotBeNull();
        completed!.CallId.Should().Be("call-1");
        completed.ToolName.Should().Be("controlled_tool");
        completed.ResultJson.Should().Be("{\"ok\":true}");
        completed.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ChatStreamAsync_CancellationAfterToolStart_ShouldYieldTerminalBeforeThrowing()
    {
        var provider = new ToolThenAnswerProvider();
        var tool = new ControlledTool();
        var tools = new ToolManager();
        tools.Register(tool);
        var runtime = new ChatRuntime(
            providerFactory: () => provider,
            history: new ChatHistory(),
            toolLoop: CreateToolCallLoop(tools),
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages = [],
                Tools = tools.GetAll(),
                ToolContext = TestToolContext,
            });
        using var cts = new CancellationTokenSource();
        await using var stream = runtime.ChatStreamAsync(
                "hello",
                maxToolRounds: 2,
                requestId: "cancelled-tool-request",
                turnCatalog: null,
                ct: cts.Token)
            .GetAsyncEnumerator(cts.Token);

        ToolCallStartedChunk? started = null;
        while (await stream.MoveNextAsync())
        {
            if (stream.Current.ToolCallStarted is not null)
            {
                started = stream.Current.ToolCallStarted;
                break;
            }
        }

        started.Should().NotBeNull();
        var pendingMove = stream.MoveNextAsync().AsTask();
        await tool.Started.Task;
        cts.Cancel();

        (await pendingMove).Should().BeTrue(
            "the tool terminal must cross the stream before cancellation is rethrown");
        var completed = stream.Current.ToolCallCompleted;
        completed.Should().NotBeNull();
        completed!.CallId.Should().Be(started!.ToolCall.Id);
        completed.OperationId.Should().Be(started.OperationId);
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be("Tool execution was cancelled.");

        var moveAfterTerminal = async () => await stream.MoveNextAsync().AsTask();
        await moveAfterTerminal.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ChatStreamAsync_TextParsedToolCall_ShouldUseSameStartedThenCompletedLifecycle()
    {
        var provider = new TextToolThenAnswerProvider();
        var tool = new ControlledTool();
        var tools = new ToolManager();
        tools.Register(tool);
        var runtime = new ChatRuntime(
            providerFactory: () => provider,
            history: new ChatHistory(),
            toolLoop: CreateToolCallLoop(tools),
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages = [],
                Tools = tools.GetAll(),
                ToolContext = TestToolContext,
            });

        await using var stream = runtime.ChatStreamAsync(
                "hello",
                maxToolRounds: 2,
                requestId: "text-tool-progress-request",
                turnCatalog: null)
            .GetAsyncEnumerator();
        while (await stream.MoveNextAsync() && stream.Current.ToolCallStarted == null)
        {
        }

        stream.Current.ToolCallStarted.Should().NotBeNull();
        stream.Current.ToolCallStarted!.ToolCall.Name.Should().Be("controlled_tool");
        tool.Started.Task.IsCompleted.Should().BeFalse();

        var resume = stream.MoveNextAsync().AsTask();
        var executionSignal = await Task.WhenAny(tool.Started.Task, resume);
        executionSignal.Should().BeSameAs(tool.Started.Task,
            "the admitted terminal must invoke the tool before publishing its result");
        resume.IsCompleted.Should().BeFalse();
        tool.Release("{\"source\":\"text\"}");
        (await resume).Should().BeTrue();

        while (stream.Current.ToolCallCompleted == null && await stream.MoveNextAsync())
        {
        }

        stream.Current.ToolCallCompleted.Should().NotBeNull();
        stream.Current.ToolCallCompleted!.ToolName.Should().Be("controlled_tool");
        stream.Current.ToolCallCompleted.ResultJson.Should().Be("{\"source\":\"text\"}");
    }

    [Fact]
    public async Task ChatStreamAsync_ShouldPublishModelStartBeforeProviderAndKeepEveryRoundIndependent()
    {
        var provider = new ControlledRoundProvider();
        var runtime = new ChatRuntime(
            providerFactory: () => provider,
            history: new ChatHistory(),
            toolLoop: CreateToolCallLoop(new ToolManager()),
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages = [],
                Model = "model-a",
            },
            suppressToolCallRoundText: true);

        await using var stream = runtime.ChatStreamAsync(
                "hello",
                maxToolRounds: 1,
                requestId: "model-progress-request",
                turnCatalog: null)
            .GetAsyncEnumerator();

        (await stream.MoveNextAsync()).Should().BeTrue();
        var started = stream.Current.LLMInvocationStarted;
        started.Should().NotBeNull();
        started!.Round.Should().Be(0);
        started.Model.Should().Be("model-a");
        started.OperationId.Should().StartWith("model-progress-request:model:0:");
        provider.Entered.Task.IsCompleted.Should().BeFalse(
            "the model start must be observable before provider execution advances");

        var providerMove = stream.MoveNextAsync().AsTask();
        var entered = await Task.WhenAny(provider.Entered.Task, providerMove);
        entered.Should().BeSameAs(provider.Entered.Task);
        providerMove.IsCompleted.Should().BeFalse();
        provider.Release();

        var lifecycle = new List<LLMStreamChunk>();
        while (await providerMove)
        {
            lifecycle.Add(stream.Current);
            providerMove = stream.MoveNextAsync().AsTask();
        }

        var completed = lifecycle
            .Select(chunk => chunk.LLMInvocationCompleted)
            .Single(item => item != null)!;
        completed.OperationId.Should().Be(started.OperationId);
        completed.Round.Should().Be(0);
        completed.Content.Should().Be("done");
        completed.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ChatStreamAsync_ModelStart_ShouldDescribeFinalAuthorizedProviderRequest()
    {
        var provider = new AnswerProvider();
        var alphaTool = new PassiveTool("alpha_tool");
        var zetaTool = new PassiveTool("zeta_tool");
        var tools = new ToolManager();
        tools.Register(alphaTool);
        tools.Register(zetaTool);
        var runtime = new ChatRuntime(
            providerFactory: () => provider,
            history: new ChatHistory(),
            toolLoop: CreateToolCallLoop(tools),
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages = [ChatMessage.System("original-system-secret")],
                Model = "original-model",
                Tools = [zetaTool, alphaTool],
            },
            llmMiddlewares:
            [
                new DelegateLlmCallMiddleware(async (context, next) =>
                {
                    context.Request = new LLMRequest
                    {
                        Messages =
                        [
                            ChatMessage.System("private-system-prompt"),
                            ChatMessage.User("authorized request Authorization: Bearer raw-secret"),
                        ],
                        RequestId = context.Request.RequestId,
                        ToolContext = context.Request.ToolContext,
                        Model = "authorized-model",
                        Tools = [zetaTool, alphaTool, alphaTool],
                    };
                    await next();
                }),
            ]);

        LLMInvocationStartedChunk? started = null;
        await foreach (var chunk in runtime.ChatStreamAsync(
                           "original user input",
                           maxToolRounds: 1,
                           requestId: "authorized-facts-request",
                           turnCatalog: null))
        {
            if (chunk.LLMInvocationStarted != null)
            {
                started = chunk.LLMInvocationStarted;
                break;
            }
        }

        started.Should().NotBeNull();
        started!.Model.Should().Be("authorized-model");
        started.Provider.Should().Be("answer");
        started.AvailableToolNames.Should().Equal("alpha_tool", "zeta_tool");
        started.InputSummary.Should().Be("authorized request Authorization: Bearer ***REDACTED***");
        started.InputSummary.Should().NotContain("private-system-prompt");
        started.InputSummary.Should().NotContain("raw-secret");
        started.InputSummary.Length.Should().BeLessThanOrEqualTo(503);
    }

    [Fact]
    public async Task ChatStreamAsync_ModelStart_ShouldRecursivelyScrubStructuredInputSummary()
    {
        var runtime = new ChatRuntime(
            providerFactory: () => new AnswerProvider(),
            history: new ChatHistory(),
            toolLoop: CreateToolCallLoop(new ToolManager()),
            hooks: null,
            requestBuilder: _ => new LLMRequest { Messages = [] });
        LLMInvocationStartedChunk? started = null;

        await foreach (var chunk in runtime.ChatStreamAsync(
                           """{"query":"status","cookie":"session=short","credentials":{"user":"alice","password":"tiny"}}""",
                           maxToolRounds: 1,
                           requestId: "structured-input-summary",
                           turnCatalog: null))
        {
            if (chunk.LLMInvocationStarted is not null)
            {
                started = chunk.LLMInvocationStarted;
                break;
            }
        }

        started.Should().NotBeNull();
        started!.InputSummary.Should().Contain("\"query\":\"status\"");
        started.InputSummary.Should().Contain("\"cookie\":\"***REDACTED***\"");
        started.InputSummary.Should().Contain("\"credentials\":\"***REDACTED***\"");
        started.InputSummary.Should().NotContain("session=short");
        started.InputSummary.Should().NotContain("alice");
        started.InputSummary.Should().NotContain("tiny");
    }

    [Fact]
    public async Task ChatStreamAsync_ReusedRequestId_ShouldCreateNewModelOperationIdentity()
    {
        var runtime = new ChatRuntime(
            providerFactory: () => new AnswerProvider(),
            history: new ChatHistory(),
            toolLoop: CreateToolCallLoop(new ToolManager()),
            hooks: null,
            requestBuilder: _ => new LLMRequest { Messages = [] });

        async Task<string> ReadOperationIdAsync()
        {
            await foreach (var chunk in runtime.ChatStreamAsync(
                               "hello",
                               maxToolRounds: 1,
                               requestId: "same-request",
                               turnCatalog: null))
            {
                if (chunk.LLMInvocationStarted != null)
                    return chunk.LLMInvocationStarted.OperationId;
            }

            throw new InvalidOperationException("Model start was not emitted.");
        }

        var first = await ReadOperationIdAsync();
        var second = await ReadOperationIdAsync();

        first.Should().NotBe(second);
        first.Should().StartWith("same-request:model:0:");
        second.Should().StartWith("same-request:model:0:");
    }

    [Fact]
    public async Task ChatStreamAsync_CancellationAfterModelStart_ShouldYieldTerminalBeforeThrowing()
    {
        var provider = new ControlledRoundProvider();
        var runtime = new ChatRuntime(
            providerFactory: () => provider,
            history: new ChatHistory(),
            toolLoop: CreateToolCallLoop(new ToolManager()),
            hooks: null,
            requestBuilder: _ => new LLMRequest
            {
                Messages = [],
                Model = "model-a",
            });
        using var cts = new CancellationTokenSource();
        await using var stream = runtime.ChatStreamAsync(
                "hello",
                maxToolRounds: 1,
                requestId: "cancelled-model-request",
                turnCatalog: null,
                ct: cts.Token)
            .GetAsyncEnumerator(cts.Token);

        (await stream.MoveNextAsync()).Should().BeTrue();
        var started = stream.Current.LLMInvocationStarted;
        started.Should().NotBeNull();

        var providerMove = stream.MoveNextAsync().AsTask();
        await provider.Entered.Task;
        cts.Cancel();

        (await providerMove).Should().BeTrue(
            "the model terminal must cross the stream before cancellation is rethrown");
        var completed = stream.Current.LLMInvocationCompleted;
        completed.Should().NotBeNull();
        completed!.OperationId.Should().Be(started!.OperationId);
        completed.Success.Should().BeFalse();
        completed.Error.Should().Be("Model invocation was cancelled.");

        var moveAfterTerminal = async () => await stream.MoveNextAsync().AsTask();
        await moveAfterTerminal.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteSingleLlmStepAsync_ShouldUseZeroBasedLifecycleRound()
    {
        var provider = new AnswerProvider();
        var runtime = new ChatRuntime(
            providerFactory: () => provider,
            history: new ChatHistory(),
            toolLoop: CreateToolCallLoop(new ToolManager()),
            hooks: null,
            requestBuilder: _ => new LLMRequest { Messages = [] });
        var lifecycle = new List<LLMStreamChunk>();

        await runtime.ExecuteSingleLlmStepAsync(
            provider,
            new LLMRequest
            {
                RequestId = "single-step-request",
                Messages = [ChatMessage.User("hello")],
            },
            CancellationToken.None,
            (chunk, _) =>
            {
                lifecycle.Add(chunk);
                return Task.CompletedTask;
            });

        var started = lifecycle.Should().ContainSingle(chunk => chunk.LLMInvocationStarted != null)
            .Which.LLMInvocationStarted!;
        var completed = lifecycle.Should().ContainSingle(chunk => chunk.LLMInvocationCompleted != null)
            .Which.LLMInvocationCompleted!;
        started.Round.Should().Be(0);
        completed.Round.Should().Be(0);
        completed.OperationId.Should().Be(started.OperationId);
        started.OperationId.Should().StartWith("single-step-request:model:0:");
    }

    private static ToolCallLoop CreateToolCallLoop(ToolManager tools) =>
        new(
            tools,
            toolExecutionPort: new AdmittedAgentToolExecutor(
                AlwaysStartingAgentToolAdmissionLedger.Instance,
                new AppendedAuditTrail(),
                new StableIdentityHasher()));

    private static AgentToolExecutionContext TestToolContext =>
        AgentToolExecutionContext.Empty with
        {
            ExecutionOwner = AgentToolExecutionOwners.HostService(nameof(ChatRuntimeToolProgressTests)),
        };

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }

    private sealed class ControlledTool : IAgentTool
    {
        private readonly TaskCompletionSource<string> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "controlled_tool";
        public string Description => "A controlled test tool.";
        public string ParametersSchema => "{}";
        public bool IsReadOnly => true;
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

        public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            Started.TrySetResult();
            return await _release.Task.WaitAsync(ct);
        }

        public void Release(string result) => _release.TrySetResult(result);
    }

    private sealed class PassiveTool(string name) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "A passive test tool.";
        public string ParametersSchema => "{}";
        public bool IsReadOnly => true;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

    private sealed class DelegateLlmCallMiddleware(
        Func<LLMCallContext, Func<Task>, Task> handler) : ILLMCallMiddleware
    {
        public Task InvokeAsync(LLMCallContext context, Func<Task> next) => handler(context, next);
    }

    private sealed class ControlledRecoveryTool : IAgentTool
    {
        private readonly TaskCompletionSource<string> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "ornn_search_skills";
        public string Description => "Searches for recovery skills.";
        public string ParametersSchema => "{}";
        public bool IsReadOnly => true;
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
        public ToolPresentationDescriptor Presentation =>
            ToolPresentationDescriptors.Generic(Name, Description);

        public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            _ = argumentsJson;
            Started.TrySetResult();
            return await _release.Task.WaitAsync(ct);
        }

        public void Release(string result) => _release.TrySetResult(result);
    }

    private sealed class AnswerProvider : ILLMProvider
    {
        public string Name => "answer";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            yield return new LLMStreamChunk { DeltaContent = "done" };
            await Task.CompletedTask;
            yield return new LLMStreamChunk { IsLast = true };
        }
    }

    private sealed class ToolThenAnswerProvider : ILLMProvider
    {
        private int _round;

        public string Name => "controlled";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            if (_round++ == 0)
            {
                yield return new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call-1",
                        Name = "controlled_tool",
                        ArgumentsJson = "{}",
                    },
                };
            }
            else
            {
                yield return new LLMStreamChunk { DeltaContent = "done" };
            }

            await Task.CompletedTask;
            yield return new LLMStreamChunk { IsLast = true };
        }
    }

    private sealed class TextToolThenAnswerProvider : ILLMProvider
    {
        private int _round;

        public string Name => "controlled-text";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            if (_round++ == 0)
            {
                yield return new LLMStreamChunk
                {
                    DeltaContent = """
                        <function_calls>
                        <invoke name="controlled_tool">
                        <parameter name="input">from text</parameter>
                        </invoke>
                        </function_calls>
                        """,
                };
            }
            else
            {
                yield return new LLMStreamChunk { DeltaContent = "done" };
            }

            await Task.CompletedTask;
            yield return new LLMStreamChunk { IsLast = true };
        }
    }

    private sealed class ControlledRoundProvider : ILLMProvider
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "controlled-round";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            Entered.TrySetResult();
            await _release.Task.WaitAsync(ct);
            yield return new LLMStreamChunk { DeltaContent = "done" };
            yield return new LLMStreamChunk { IsLast = true };
        }

        public void Release() => _release.TrySetResult();
    }
}
