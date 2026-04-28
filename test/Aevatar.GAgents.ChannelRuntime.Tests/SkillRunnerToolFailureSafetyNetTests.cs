using System.Reflection;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core;
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
///   - <see cref="NyxIdProxyToolFailureCountingMiddleware"/>: classification + counting
///   - <see cref="SkillRunnerGAgent.EnsureToolStatusAllowsCompletion"/>: failure policy
///   - End-to-end wiring: middleware registered on the agent feeds the agent's counter
///
/// We deliberately don't drive the full LLM loop in these tests — see the existing
/// SkillRunnerGAgentTests pattern: ChatStreamAsync requires a live LLM provider, and the
/// production behaviour is fully determined by the four-piece pipeline above.
/// </summary>
public class SkillRunnerToolFailureSafetyNetTests
{
    // ─── Counter primitive ───

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

    // ─── Classification ───

    [Theory]
    [InlineData("""{"error":true,"status":401,"body":"{\"message\":\"Bad credentials\"}"}""")]
    [InlineData("""{"error":"unauthorized"}""")]
    public void Classify_NyxIdNon2xxOrErrorEnvelope_IsError(string result)
    {
        // NyxIdApiClient.SendAsync wraps every upstream non-2xx (and exceptions) as
        // {error: true|"...", status, body}. The classifier must catch any truthy `error`
        // payload, otherwise transient proxy failures land as fake-success runs.
        NyxIdProxyToolFailureCountingMiddleware.ClassifyResult(result)
            .Should().Be(NyxIdProxyToolFailureCountingMiddleware.ResultClassification.Error);
    }

    [Fact]
    public void Classify_NyxIdApprovalEnvelope_IsError()
    {
        // NyxID approval gate (codes 7000/7001) blocks the proxy until the user approves.
        // The data was not retrieved, so the call is a failure from the runner's view.
        // The `message` field paired with non-zero `code` is what makes this match the
        // narrowed envelope rule.
        var input = """{"code":7000,"approval_request_id":"req-1","message":"approval_required"}""";

        NyxIdProxyToolFailureCountingMiddleware.ClassifyResult(input)
            .Should().Be(NyxIdProxyToolFailureCountingMiddleware.ResultClassification.Error);
    }

    [Fact]
    public void Classify_LarkBusinessErrorEnvelope_IsError()
    {
        // Lark returns business errors as HTTP 200 with `code != 0` AND `msg`. The pair
        // is what makes this an envelope (not a domain field), so the classifier flags it.
        var input = """{"code":230002,"msg":"Bot is not in the chat"}""";

        NyxIdProxyToolFailureCountingMiddleware.ClassifyResult(input)
            .Should().Be(NyxIdProxyToolFailureCountingMiddleware.ResultClassification.Error);
    }

    [Fact]
    public void Classify_DomainFieldNamedCode_IsOk()
    {
        // PR #471 reviewer concern: `nyxid_proxy` is a general proxy, not Lark-specific.
        // A legitimate downstream domain response that happens to use a top-level `code`
        // field for non-error meaning (no paired `msg`/`message`) must NOT be flagged as
        // an error — otherwise a successful single-proxy-call run trips the safety net.
        var input = """{"code":42,"data":{"id":"x"}}""";

        NyxIdProxyToolFailureCountingMiddleware.ClassifyResult(input)
            .Should().Be(NyxIdProxyToolFailureCountingMiddleware.ResultClassification.Ok);
    }

    [Fact]
    public void Classify_GitHubSuccessShape_IsOk()
    {
        // GitHub /search/* success: `total_count` + `items`. No envelope markers.
        var input = """{"total_count":52,"incomplete_results":false,"items":[{"sha":"abc"}]}""";

        NyxIdProxyToolFailureCountingMiddleware.ClassifyResult(input)
            .Should().Be(NyxIdProxyToolFailureCountingMiddleware.ResultClassification.Ok);
    }

    [Fact]
    public void Classify_LarkBusinessSuccessCode_IsOk()
    {
        // Lark business success carries `code: 0`. Must not be classified as error just
        // because a `code` field is present.
        var input = """{"code":0,"msg":"success","data":{"message_id":"om_1"}}""";

        NyxIdProxyToolFailureCountingMiddleware.ClassifyResult(input)
            .Should().Be(NyxIdProxyToolFailureCountingMiddleware.ResultClassification.Ok);
    }

    [Fact]
    public void Classify_ErrorFieldExplicitFalse_IsOk()
    {
        var input = """{"error":false,"data":{"id":"x"}}""";

        NyxIdProxyToolFailureCountingMiddleware.ClassifyResult(input)
            .Should().Be(NyxIdProxyToolFailureCountingMiddleware.ResultClassification.Ok);
    }

    [Fact]
    public void Classify_JsonArrayResponse_IsOk()
    {
        // Codex review (PR #471): discovery responses and list endpoints return JSON
        // arrays, not objects. They must classify as ok so a successful array call in a
        // mixed run keeps the success counter > 0 and the safety net does not fire.
        var input = """[{"slug":"api-github"},{"slug":"api-lark-bot"}]""";

        NyxIdProxyToolFailureCountingMiddleware.ClassifyResult(input)
            .Should().Be(NyxIdProxyToolFailureCountingMiddleware.ResultClassification.Ok);
    }

    [Fact]
    public void Classify_NonJsonOrEmpty_IsUnknown()
    {
        // The classifier stays out of cases it can't read, rather than guessing.
        NyxIdProxyToolFailureCountingMiddleware.ClassifyResult("plain text body")
            .Should().Be(NyxIdProxyToolFailureCountingMiddleware.ResultClassification.Unknown);
        NyxIdProxyToolFailureCountingMiddleware.ClassifyResult(string.Empty)
            .Should().Be(NyxIdProxyToolFailureCountingMiddleware.ResultClassification.Unknown);
        NyxIdProxyToolFailureCountingMiddleware.ClassifyResult(null)
            .Should().Be(NyxIdProxyToolFailureCountingMiddleware.ResultClassification.Unknown);
    }

    // ─── Middleware behaviour ───

    [Fact]
    public async Task Middleware_OnNyxIdProxyError_IncrementsFailureCount()
    {
        var counter = new SkillRunnerToolFailureCounter();
        var middleware = new NyxIdProxyToolFailureCountingMiddleware(counter);
        var ctx = BuildContext("nyxid_proxy", result: """{"error":true,"status":401}""");

        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        counter.FailureCount.Should().Be(1);
        counter.SuccessCount.Should().Be(0);
    }

    [Fact]
    public async Task Middleware_OnNyxIdProxyOk_IncrementsSuccessCount()
    {
        var counter = new SkillRunnerToolFailureCounter();
        var middleware = new NyxIdProxyToolFailureCountingMiddleware(counter);
        var ctx = BuildContext("nyxid_proxy", result: """{"total_count":12,"items":[]}""");

        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        counter.FailureCount.Should().Be(0);
        counter.SuccessCount.Should().Be(1);
    }

    [Fact]
    public async Task Middleware_DoesNotMutateResult()
    {
        // The classifier must not modify the LLM-visible response. The previous design
        // injected a marker field that risked being echoed by weaker models — this test
        // pins that we read the body in place.
        var counter = new SkillRunnerToolFailureCounter();
        var middleware = new NyxIdProxyToolFailureCountingMiddleware(counter);
        const string body = """{"total_count":12,"items":[{"sha":"abc"}]}""";
        var ctx = BuildContext("nyxid_proxy", result: body);

        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        ctx.Result.Should().Be(body);
    }

    [Fact]
    public async Task Middleware_IgnoresOtherTools()
    {
        // Other tools may have their own success semantics and are intentionally outside
        // the safety net's scope.
        var counter = new SkillRunnerToolFailureCounter();
        var middleware = new NyxIdProxyToolFailureCountingMiddleware(counter);
        var ctx = BuildContext("not_nyxid_proxy", result: """{"error":true}""");

        await middleware.InvokeAsync(ctx, () => Task.CompletedTask);

        counter.FailureCount.Should().Be(0);
        counter.SuccessCount.Should().Be(0);
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
            ctx.Result = """{"error":true}""";
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
        // Skills that don't fan out to nyxid_proxy at all (e.g. pure LLM transformations)
        // must not be tripped by the safety net. Note: this also lets a pathological run
        // through where the LLM ignored all tools and hallucinated a report. The reviewer
        // flagged this for the daily-report skill specifically; addressing "expected tool
        // never called" is out of scope for this PR — it would need per-skill policy that
        // doesn't generalize to other scheduled skills.
        var act = () => SkillRunnerGAgent.EnsureToolStatusAllowsCompletion(failureCount: 0, successCount: 0);

        act.Should().NotThrow();
    }

    // ─── End-to-end wiring ───

    [Fact]
    public async Task Wiring_MiddlewareRegisteredOnAgent_FeedsAgentCounter()
    {
        // The previous wiring assertion was tautological (compared the test-only accessor
        // to itself). Drive the middleware that AIGAgentBase actually registered for this
        // agent and verify the same counter the runner reads in
        // EnsureToolStatusAllowsCompletion gets incremented. This catches regressions in
        // BuildToolMiddlewareChain where the counter could be detached from the
        // middleware that the chat loop runs.
        var agent = new SkillRunnerGAgent();

        var registeredField = typeof(AIGAgentBase<SkillRunnerState>).GetField(
            "_toolMiddlewares", BindingFlags.Instance | BindingFlags.NonPublic);
        registeredField.Should().NotBeNull();
        var registered = (IReadOnlyList<IToolCallMiddleware>?)registeredField!.GetValue(agent);
        registered.Should().NotBeNull();

        var registeredCounting = registered!
            .OfType<NyxIdProxyToolFailureCountingMiddleware>()
            .Should().ContainSingle("the runner appends exactly one counting middleware to the chain")
            .Subject;

        var ctx = BuildContext("nyxid_proxy", result: """{"error":true,"status":502}""");
        await registeredCounting.InvokeAsync(ctx, () => Task.CompletedTask);

        agent.ToolFailureCounterForTesting.FailureCount.Should().Be(1);
    }

    [Fact]
    public void Wiring_PreservesCallerInjectedMiddleware()
    {
        // DI may pre-register middleware (e.g., the org-wide approval middleware). The
        // counting middleware must be appended, not overwrite — otherwise wiring this
        // safety net into a DI graph would silently drop existing middleware behaviour.
        var injected = new RecordingMiddleware();
        var agent = new SkillRunnerGAgent(toolMiddlewares: new IToolCallMiddleware[] { injected });

        var registeredField = typeof(AIGAgentBase<SkillRunnerState>).GetField(
            "_toolMiddlewares", BindingFlags.Instance | BindingFlags.NonPublic);
        var registered = (IReadOnlyList<IToolCallMiddleware>?)registeredField!.GetValue(agent);

        registered.Should().Contain(injected, "caller-injected middleware must survive");
        registered.Should().ContainSingle(m => m is NyxIdProxyToolFailureCountingMiddleware);
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

    private sealed class RecordingMiddleware : IToolCallMiddleware
    {
        public Task InvokeAsync(ToolCallContext context, Func<Task> next) => next();
    }
}
