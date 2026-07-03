using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Middleware;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Middleware;

/// <summary>
/// M6 (middleware half): once a connected-service write resolves to <c>IsDestructive == true</c>
/// (the fail-closed default for non-GET/HEAD/OPTIONS methods), <see cref="ToolApprovalMiddleware"/>
/// must route it through the approval handler under <see cref="ToolApprovalMode.Auto"/> rather than
/// letting it pass. A read (<c>IsDestructive == false</c>) still bypasses. This locks in the
/// destructive → approval-required linkage that <c>ConnectedServiceProxyTool</c> now relies on.
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
            // Shape of an unmarked connected-service write after the M6 fail-closed default.
            Tool = new DestructiveAutoTool("nyxid_api-shop__create_order", isReadOnly: false, isDestructive: true),
            ToolName = "nyxid_api-shop__create_order",
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
            // Shape of a GET connected-service operation: read-only, non-destructive.
            Tool = new DestructiveAutoTool("nyxid_api-shop__get_order", isReadOnly: true, isDestructive: false),
            ToolName = "nyxid_api-shop__get_order",
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
        public string Description => "connected-service proxy tool under test";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;
        public bool IsReadOnly { get; } = isReadOnly;
        public bool IsDestructive { get; } = isDestructive;

        // null → middleware falls through to the static IsReadOnly/IsDestructive classifier,
        // mirroring ConnectedServiceProxyTool which does not override RequiresApproval.
        public bool? RequiresApproval(string argumentsJson) => null;

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }
}
