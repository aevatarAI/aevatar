using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Middleware;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Middleware;

/// <summary>
/// Under <see cref="ToolApprovalMode.Auto"/>, destructive tools must pass through approval while
/// non-destructive reads may proceed without an approval request.
/// </summary>
public class ToolApprovalDestructiveDefaultTests
{
    [Fact]
    public async Task AutoMode_DestructiveWrite_RequiresApproval()
    {
        var handler = new RecordingApprovalHandler(ToolApprovalResult.Denied("needs review"));
        var middleware = new ToolApprovalMiddleware(handler);
        var ctx = new ToolCallContext
        {
            Tool = new DestructiveAutoTool("operation_write", isReadOnly: false, isDestructive: true),
            ToolName = "operation_write",
            ToolCallId = "tc-write-1",
            ArgumentsJson = "{}",
        };

        var nextExecuted = false;
        await middleware.InvokeAsync(ctx, () =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });

        nextExecuted.Should().BeFalse("a destructive write must not run before approval");
        handler.Requests.Should().ContainSingle("the destructive write is escalated to the approval handler");
        ctx.Terminate.Should().BeTrue();
        ctx.TerminationKind.Should().Be(ToolCallTerminationKind.ApprovalDenied);
    }

    [Fact]
    public async Task AutoMode_SafeRead_BypassesApproval()
    {
        var handler = new RecordingApprovalHandler(ToolApprovalResult.Denied("should-not-be-asked"));
        var middleware = new ToolApprovalMiddleware(handler);
        var ctx = new ToolCallContext
        {
            Tool = new DestructiveAutoTool("operation_read", isReadOnly: true, isDestructive: false),
            ToolName = "operation_read",
            ToolCallId = "tc-read-1",
            ArgumentsJson = "{}",
        };

        var nextExecuted = false;
        await middleware.InvokeAsync(ctx, () =>
        {
            nextExecuted = true;
            return Task.CompletedTask;
        });

        nextExecuted.Should().BeTrue("a safe read bypasses approval under Auto");
        handler.Requests.Should().BeEmpty("no approval is requested for a non-destructive read");
        ctx.Terminate.Should().BeFalse();
    }

    private sealed class RecordingApprovalHandler(params ToolApprovalResult[] results) : IToolApprovalHandler
    {
        private readonly Queue<ToolApprovalResult> _results = new(results);

        public List<ToolApprovalRequest> Requests { get; } = [];

        public Task<ToolApprovalResult> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_results.TryDequeue(out var r) ? r : ToolApprovalResult.Denied("no-script"));
        }
    }

    private sealed class DestructiveAutoTool(string name, bool isReadOnly, bool isDestructive) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "approval policy test tool";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;
        public bool IsReadOnly { get; } = isReadOnly;
        public bool IsDestructive { get; } = isDestructive;

        public bool? RequiresApproval(string argumentsJson) => null;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }
}
