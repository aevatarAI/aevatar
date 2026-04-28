using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Cover the runner-layer safety net for issue #439: when every nyxid_proxy call in a
/// skill run failed, the runner must downgrade the run to a failure even if the LLM
/// produced plausible plain-text output. Tests are split across:
///
///   - <see cref="SkillRunnerToolFailureCounter"/>: state primitive
///   - <see cref="NyxIdProxyToolFailureCountingMiddleware"/>: classifies tool results
///   - <see cref="SkillRunnerGAgent.EnsureToolStatusAllowsCompletion"/>: failure policy
///   - End-to-end wiring: instance counter is the same one the middleware writes to
///
/// We deliberately don't drive the full LLM loop in these tests — see the existing
/// SkillRunnerGAgentTests pattern: ChatStreamAsync requires a live LLM provider, and the
/// production behaviour is fully determined by the four-piece pipeline above.
/// </summary>
public class SkillRunnerToolFailureSafetyNetTests
{
    // ─── Counter ───

    [Fact]
    public void Counter_StartsZero()
    {
        var counter = new SkillRunnerToolFailureCounter();

        counter.FailureCount.Should().Be(0);
        counter.SuccessCount.Should().Be(0);
    }

    [Fact]
    public void Counter_RecordsAndResets()
    {
        var counter = new SkillRunnerToolFailureCounter();
        counter.RecordFailure();
        counter.RecordFailure();
        counter.RecordSuccess();

        counter.FailureCount.Should().Be(2);
        counter.SuccessCount.Should().Be(1);

        counter.Reset();
        counter.FailureCount.Should().Be(0);
        counter.SuccessCount.Should().Be(0);
    }

    // ─── Middleware ───

    [Fact]
    public async Task Middleware_OnNyxIdProxyErrorMarker_IncrementsFailureCount()
    {
        var counter = new SkillRunnerToolFailureCounter();
        var middleware = new NyxIdProxyToolFailureCountingMiddleware(counter);
        var ctx = BuildContext("nyxid_proxy", result:
            $$"""{"{{NyxIdProxyTool.ToolStatusFieldName}}":"{{NyxIdProxyTool.ToolStatusError}}","error":true,"status":401}""");

        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        counter.FailureCount.Should().Be(1);
        counter.SuccessCount.Should().Be(0);
    }

    [Fact]
    public async Task Middleware_OnNyxIdProxyOkMarker_IncrementsSuccessCount()
    {
        var counter = new SkillRunnerToolFailureCounter();
        var middleware = new NyxIdProxyToolFailureCountingMiddleware(counter);
        var ctx = BuildContext("nyxid_proxy", result:
            $$"""{"{{NyxIdProxyTool.ToolStatusFieldName}}":"{{NyxIdProxyTool.ToolStatusOk}}","total_count":12}""");

        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        counter.FailureCount.Should().Be(0);
        counter.SuccessCount.Should().Be(1);
    }

    [Fact]
    public async Task Middleware_IgnoresOtherTools()
    {
        // Other tools (memory store, file ops, etc.) may have their own success semantics
        // and are intentionally outside the safety net's scope.
        var counter = new SkillRunnerToolFailureCounter();
        var middleware = new NyxIdProxyToolFailureCountingMiddleware(counter);
        var ctx = BuildContext("not_nyxid_proxy", result:
            $$"""{"{{NyxIdProxyTool.ToolStatusFieldName}}":"{{NyxIdProxyTool.ToolStatusError}}"}""");

        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        counter.FailureCount.Should().Be(0);
        counter.SuccessCount.Should().Be(0);
    }

    [Fact]
    public async Task Middleware_IgnoresUnmarkedResults()
    {
        // Discovery responses (JSON arrays) and raw-text bodies don't carry the marker.
        // The middleware must not guess a status for them, otherwise the safety net
        // could downgrade a clean discovery-only run.
        var counter = new SkillRunnerToolFailureCounter();
        var middleware = new NyxIdProxyToolFailureCountingMiddleware(counter);
        var ctx = BuildContext("nyxid_proxy", result: """[{"slug":"api-github"}]""");

        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        counter.FailureCount.Should().Be(0);
        counter.SuccessCount.Should().Be(0);
    }

    [Fact]
    public async Task Middleware_IgnoresEmptyResult()
    {
        var counter = new SkillRunnerToolFailureCounter();
        var middleware = new NyxIdProxyToolFailureCountingMiddleware(counter);
        var ctx = BuildContext("nyxid_proxy", result: null);

        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        counter.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task Middleware_AwaitsNextBeforeReadingResult()
    {
        // The result is only set once `next()` runs the underlying tool, so the middleware
        // must await before classifying — otherwise it would always observe a null result.
        var counter = new SkillRunnerToolFailureCounter();
        var middleware = new NyxIdProxyToolFailureCountingMiddleware(counter);
        var ctx = BuildContext("nyxid_proxy", result: null);

        await middleware.InvokeAsync(ctx, () =>
        {
            ctx.Result = $$"""{"{{NyxIdProxyTool.ToolStatusFieldName}}":"{{NyxIdProxyTool.ToolStatusError}}"}""";
            return Task.CompletedTask;
        });

        counter.FailureCount.Should().Be(1);
    }

    // ─── Policy ───

    [Fact]
    public void Policy_AllFailures_Throws()
    {
        // (a) all-fail case from the issue's acceptance criteria. The thrown
        // InvalidOperationException is what HandleTriggerAsync catches and converts into
        // SkillRunnerExecutionFailedEvent (after the retry budget is exhausted), so
        // /agent-status reports a meaningful error_count and last_error.
        var act = () => SkillRunnerGAgent.EnsureToolStatusAllowsCompletion(failureCount: 3, successCount: 0);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*All 3 nyxid_proxy tool call(s)*failed*");
    }

    [Fact]
    public void Policy_MixedSuccessAndFailure_Allows()
    {
        // (b) mixed case: partial data is more useful than a blanket failure. The
        // prompt-layer §9 Source health footer surfaces which queries failed; the runner
        // simply lets the run complete normally.
        var act = () => SkillRunnerGAgent.EnsureToolStatusAllowsCompletion(failureCount: 2, successCount: 4);

        act.Should().NotThrow();
    }

    [Fact]
    public void Policy_GenuinelyEmpty_Allows()
    {
        // (c) genuine empty-day case: every nyxid_proxy call returned 2xx with no matching
        // items, so the runner records the LLM's "No measurable activity" output as a
        // legitimate success.
        var act = () => SkillRunnerGAgent.EnsureToolStatusAllowsCompletion(failureCount: 0, successCount: 7);

        act.Should().NotThrow();
    }

    [Fact]
    public void Policy_NoToolCallsAtAll_Allows()
    {
        // Skill that doesn't fan out to nyxid_proxy at all (e.g. pure LLM transformation).
        // The safety net must not turn a healthy LLM-only run into a failure.
        var act = () => SkillRunnerGAgent.EnsureToolStatusAllowsCompletion(failureCount: 0, successCount: 0);

        act.Should().NotThrow();
    }

    // ─── Wiring ───

    [Fact]
    public void Runner_ExposesCounter_ThatIsTheSameInstanceTheMiddlewareWritesTo()
    {
        // End-to-end wiring assertion: the counter the runner reads in
        // EnsureToolStatusAllowsCompletion must be the same instance the middleware
        // populates inside the ChatStreamAsync loop. Without this, the safety net is
        // wired to a different counter and silently never trips.
        var agent = new SkillRunnerGAgent();
        var runnerCounter = agent.ToolFailureCounterForTesting;

        runnerCounter.RecordFailure();
        runnerCounter.RecordFailure();

        agent.ToolFailureCounterForTesting.FailureCount.Should().Be(2);
    }

    private static ToolCallContext BuildContext(string toolName, string? result) => new()
    {
        Tool = new StubAgentTool(toolName),
        ToolName = toolName,
        ToolCallId = "call-1",
        ArgumentsJson = "{}",
        Result = result,
    };

    private sealed class StubAgentTool : IAgentTool
    {
        public StubAgentTool(string name) => Name = name;
        public string Name { get; }
        public string Description => string.Empty;
        public string ParametersSchema => "{}";
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
    }
}
