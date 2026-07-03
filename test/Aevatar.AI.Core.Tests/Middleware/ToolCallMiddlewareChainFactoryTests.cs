using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Auditing;
using Aevatar.AI.Core.Middleware;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Middleware;

public sealed class ToolCallMiddlewareChainFactoryTests
{
    [Fact]
    public async Task ForAgentRuntime_ShouldPrependCanonicalCredentialAndApprovalMiddlewares()
    {
        var custom = new RecordingMiddleware();

        var chain = ToolCallMiddlewareChainFactory.ForAgentRuntime(
            [custom],
            new ScriptedApprovalHandler(ToolApprovalResult.Approved()),
            null);

        chain.Should().HaveCount(3);
        chain[0].Should().BeOfType<ToolCallCredentialPolicyMiddleware>();
        chain[1].Should().BeOfType<ToolApprovalMiddleware>();
        chain[2].Should().BeSameAs(custom);

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

        chain.Should().HaveCount(3);
        chain[0].Should().BeOfType<ToolCallCredentialPolicyMiddleware>();
        chain.Should().ContainSingle(middleware => middleware is ToolApprovalMiddleware);
        chain[2].Should().BeSameAs(custom);

        var context = NewContext();
        await RunChainAsync(chain, context);

        duplicateHandler.Requests.Should().BeEmpty();
        custom.Executions.Should().Be(1);
        context.Terminate.Should().BeFalse();
        context.Result.Should().Be("""{"ok":true}""");
    }

    [Fact]
    public void ForAgentRuntime_ShouldRemoveExternallyRegisteredCredentialPolicyMiddleware()
    {
        var custom = new RecordingMiddleware();

        var chain = ToolCallMiddlewareChainFactory.ForAgentRuntime(
            [new ToolCallCredentialPolicyMiddleware(), custom],
            new ScriptedApprovalHandler(ToolApprovalResult.Approved()),
            null);

        chain.Should().HaveCount(3);
        chain.Should().ContainSingle(middleware => middleware is ToolCallCredentialPolicyMiddleware);
        chain[0].Should().BeOfType<ToolCallCredentialPolicyMiddleware>();
        chain[2].Should().BeSameAs(custom);
    }

    [Fact]
    public async Task ForAgentRuntime_ShouldHoistSingleAuditMiddlewareAsOutermostTerminalObserver()
    {
        var events = new List<string>();
        var custom = new RecordingMiddleware(events, "custom");
        var audit = NewAuditMiddleware(events);
        var duplicateAudit = NewAuditMiddleware(events);

        var chain = ToolCallMiddlewareChainFactory.ForAgentRuntime(
            [custom, audit, new ToolCallCredentialPolicyMiddleware(), duplicateAudit],
            new ScriptedApprovalHandler(ToolApprovalResult.Approved()),
            null);

        chain.Should().HaveCount(4);
        chain[0].Should().BeSameAs(audit);
        chain[1].Should().BeOfType<ToolCallCredentialPolicyMiddleware>();
        chain[2].Should().BeOfType<ToolApprovalMiddleware>();
        chain[3].Should().BeSameAs(custom);
        chain.Should().ContainSingle(middleware => middleware is ToolExecutionAuditMiddleware);

        await RunChainAsync(chain, NewContext());

        events.Should().Equal(
            "custom:before",
            "core",
            "custom:after",
            "audit:append");
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
            if (context.Items.TryGetValue("events", out var value) && value is List<string> events)
                events.Add("core");
            context.Result = """{"ok":true}""";
            return Task.CompletedTask;
        });

    private sealed class RecordingMiddleware : IToolCallMiddleware
    {
        private readonly List<string>? _events;
        private readonly string? _name;

        public RecordingMiddleware()
        {
        }

        public RecordingMiddleware(List<string> events, string name)
        {
            _events = events;
            _name = name;
        }

        public int Executions { get; private set; }

        public async Task InvokeAsync(ToolCallContext context, Func<Task> next)
        {
            Executions++;
            if (_events != null)
            {
                context.Items["events"] = _events;
                _events.Add($"{_name}:before");
            }

            await next();

            _events?.Add($"{_name}:after");
        }
    }

    private static ToolExecutionAuditMiddleware NewAuditMiddleware(List<string> events) =>
        new(
            new RecordingAuditTrailAppender(events),
            new ToolAuditRecordFactory(new StableAuditActorIdentityHasher()));

    private sealed class RecordingAuditTrailAppender(List<string> events) : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            events.Add("audit:append");
            return Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId, record.AuditActorId, DateTimeOffset.UtcNow));
        }
    }

    private sealed class StableAuditActorIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) =>
            new($"audit:{canonicalActorKey}", "test-key");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) =>
            string.Equals(auditActorId, $"audit:{canonicalActorKey}", StringComparison.Ordinal) &&
            string.Equals(identityKeyId, "test-key", StringComparison.Ordinal);
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
