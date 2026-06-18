using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Middleware;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Middleware;

public sealed class ToolCallMiddlewareChainFactoryTests
{
    [Fact]
    public async Task ForAgentRuntime_ShouldPrependCanonicalApprovalMiddleware()
    {
        var custom = new RecordingMiddleware();

        var chain = ToolCallMiddlewareChainFactory.ForAgentRuntime(
            [custom],
            new ScriptedApprovalHandler(ToolApprovalResult.Approved()),
            null);

        chain.Should().HaveCount(2);
        chain[0].Should().BeOfType<ToolApprovalMiddleware>();
        chain[1].Should().BeSameAs(custom);

        var context = NewContext();
        await RunChainAsync(chain, context);

        custom.Executions.Should().Be(1);
        context.Result.Should().Be("""{"ok":true}""");
    }

    [Fact]
    public async Task ForAgentRuntime_ShouldRemoveExternallyRegisteredApprovalMiddleware()
    {
        var custom = new RecordingMiddleware();
        var duplicateHandler = new ScriptedApprovalHandler(ToolApprovalResult.Denied("duplicate"));

        var chain = ToolCallMiddlewareChainFactory.ForAgentRuntime(
            [new ToolApprovalMiddleware(duplicateHandler), custom],
            new ScriptedApprovalHandler(ToolApprovalResult.Approved()),
            null);

        chain.Should().HaveCount(2);
        chain.Should().ContainSingle(middleware => middleware is ToolApprovalMiddleware);
        chain[1].Should().BeSameAs(custom);

        var context = NewContext();
        await RunChainAsync(chain, context);

        duplicateHandler.Requests.Should().BeEmpty();
        custom.Executions.Should().Be(1);
        context.Terminate.Should().BeFalse();
        context.Result.Should().Be("""{"ok":true}""");
    }

    private static ToolCallContext NewContext() => new()
    {
        Tool = new FakeAgentTool(),
        ToolName = "danger",
        ToolCallId = "call-1",
        ArgumentsJson = "{}",
    };

    private static Task RunChainAsync(IReadOnlyList<IToolCallMiddleware> chain, ToolCallContext context) =>
        MiddlewarePipeline.RunToolCallAsync(chain, context, () =>
        {
            context.Result = """{"ok":true}""";
            return Task.CompletedTask;
        });

    private sealed class RecordingMiddleware : IToolCallMiddleware
    {
        public int Executions { get; private set; }

        public async Task InvokeAsync(ToolCallContext context, Func<Task> next)
        {
            Executions++;
            await next();
        }
    }

    private sealed class ScriptedApprovalHandler(params ToolApprovalResult[] results) : IToolApprovalHandler
    {
        private readonly Queue<ToolApprovalResult> _results = new(results);

        public List<ToolApprovalRequest> Requests { get; } = [];

        public Task<ToolApprovalResult> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_results.TryDequeue(out var result)
                ? result
                : ToolApprovalResult.Denied("missing scripted result"));
        }
    }

    private sealed class FakeAgentTool : IAgentTool
    {
        public string Name => "danger";
        public string Description => "fake";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.AlwaysRequire;
        public bool IsDestructive => true;
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) => Task.FromResult("{}");
    }
}
