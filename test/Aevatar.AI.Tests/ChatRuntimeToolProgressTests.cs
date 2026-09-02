using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
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
            stream.Current.ToolCallStarted!.ToolCall.Name.Should().Be("use_skill");
            stream.Current.ToolCallStarted.Presentation.Kind.Should().Be(ToolPresentationKind.Skill);
            stream.Current.ToolCallStarted.Presentation.Skill.SkillName.Should().Be("project-summary");
            tool.Started.Task.IsCompleted.Should().BeFalse();

            var resume = stream.MoveNextAsync().AsTask();
            var executionSignal = await Task.WhenAny(tool.Started.Task, resume);
            executionSignal.Should().BeSameAs(tool.Started.Task,
                "the admitted terminal must invoke the recovery tool before publishing its result");
            resume.IsCompleted.Should().BeFalse();
            tool.Release("{\"loaded\":true}");
            (await resume).Should().BeTrue();
            stream.Current.ToolCallCompleted.Should().NotBeNull();
            stream.Current.ToolCallCompleted!.ToolName.Should().Be("use_skill");
            stream.Current.ToolCallCompleted.ResultJson.Should().Be("{\"loaded\":true}");
        }
        finally
        {
            tool.Release("{\"loaded\":true}");
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

    private sealed class ControlledRecoveryTool : IAgentTool
    {
        private readonly TaskCompletionSource<string> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "use_skill";
        public string Description => "Loads one recovery skill.";
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
            ToolPresentationDescriptors.Skill(Name, "Use skill", Description, string.Empty, "test");

        public ToolPresentationDescriptor ResolvePresentation(string argumentsJson) =>
            ToolPresentationDescriptors.Skill(
                Name,
                "project-summary",
                Description,
                argumentsJson.Contains("project-summary", StringComparison.Ordinal)
                    ? "project-summary"
                    : string.Empty,
                "test");

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
}
