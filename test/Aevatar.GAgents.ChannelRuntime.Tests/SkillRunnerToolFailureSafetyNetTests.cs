using System.Reflection;
using Aevatar.AI.Core;
using Aevatar.AI.Core.Hooks;
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
///   - <see cref="NyxIdProxyToolFailureCountingMiddleware"/>: classification + counting hook
///   - <see cref="SkillRunnerGAgent.EnsureToolStatusAllowsCompletion"/>: failure policy
///   - End-to-end wiring: hook registered on the agent feeds the agent's counter
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
        counter.FirstFailure.Should().BeNull();
        counter.LatestFailure.Should().BeNull();
    }

    [Fact]
    public void Counter_RecordsFailureSamples()
    {
        var counter = new SkillRunnerToolFailureCounter();
        var first = new SkillRunnerToolFailureSample(
            "api-github",
            "GET",
            "/repos/org/repo/milestones",
            404,
            "Not Found");
        var latest = new SkillRunnerToolFailureSample(
            "api-github",
            "GET",
            "/repos/org/repo/issues",
            404,
            "Not Found");

        counter.RecordFailure(first);
        counter.RecordFailure(latest);

        counter.FirstFailure.Should().BeSameAs(first);
        counter.LatestFailure.Should().BeSameAs(latest);
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

    [Theory]
    [InlineData(7000)]
    [InlineData(7001)]
    public void Classify_NyxIdApprovalCode_IsError(long code)
    {
        // NyxID approval-required (7000) and approval-rejected (7001) block the proxy:
        // the data was not retrieved, so the call counts as a failure. The classifier
        // matches the code directly (mirroring the existing IsApprovalError detection)
        // rather than relying on a paired message field that future NyxID payloads
        // could omit.
        var input = $$"""{"code":{{code}},"approval_request_id":"req-1","message":"approval_required"}""";

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

    [Theory]
    [InlineData("""{"code":42,"data":{"id":"x"}}""")]
    [InlineData("""{"code":200,"message":"success","data":{}}""")]
    [InlineData("""{"code":1,"message":"ok"}""")]
    public void Classify_GenericCodeFieldWithoutLarkMsg_IsOk(string input)
    {
        // PR #471 reviewer concern (round 2): `nyxid_proxy` is a general proxy, not
        // Lark-specific. Generic SaaS APIs commonly return `{"code": 200, "message":
        // "success"}` style success envelopes; the previous narrowed rule still
        // false-flagged these because it accepted `code != 0` paired with `message`.
        // The classifier now requires the Lark-specific short field `msg` (or one of
        // the known NyxID approval codes) — generic `code + message` envelopes pass
        // through as ok.
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

    // ─── Hook behaviour ───

    [Fact]
    public async Task Middleware_OnNyxIdProxyError_IncrementsFailureCount()
    {
        var counter = new SkillRunnerToolFailureCounter();
        var hook = new NyxIdProxyToolFailureCountingMiddleware(counter);
        var ctx = BuildContext(
            "nyxid_proxy",
            result: """{"error":true,"status":401,"body":"{\"message\":\"Bad credentials\"}"}""",
            argumentsJson: """{"slug":"api-github","method":"GET","path":"/repos/org/repo/issues"}""");

        await hook.OnToolExecuteEndAsync(ctx, CancellationToken.None);

        counter.FailureCount.Should().Be(1);
        counter.SuccessCount.Should().Be(0);
        counter.LatestFailure.Should().NotBeNull();
        counter.LatestFailure!.ToDiagnosticString()
            .Should().Be("api-github GET /repos/org/repo/issues -> HTTP 401: Bad credentials");
    }

    [Fact]
    public async Task Middleware_OnNyxIdProxyOk_IncrementsSuccessCount()
    {
        var counter = new SkillRunnerToolFailureCounter();
        var hook = new NyxIdProxyToolFailureCountingMiddleware(counter);
        var ctx = BuildContext("nyxid_proxy", result: """{"total_count":12,"items":[]}""");

        await hook.OnToolExecuteEndAsync(ctx, CancellationToken.None);

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
        var hook = new NyxIdProxyToolFailureCountingMiddleware(counter);
        const string body = """{"total_count":12,"items":[{"sha":"abc"}]}""";
        var ctx = BuildContext("nyxid_proxy", result: body);

        await hook.OnToolExecuteEndAsync(ctx, CancellationToken.None);

        ctx.ToolResult.Should().Be(body);
    }

    [Fact]
    public async Task Middleware_IgnoresOtherTools()
    {
        // Other tools may have their own success semantics and are intentionally outside
        // the safety net's scope.
        var counter = new SkillRunnerToolFailureCounter();
        var hook = new NyxIdProxyToolFailureCountingMiddleware(counter);
        var ctx = BuildContext("not_nyxid_proxy", result: """{"error":true}""");

        await hook.OnToolExecuteEndAsync(ctx, CancellationToken.None);

        counter.FailureCount.Should().Be(0);
        counter.SuccessCount.Should().Be(0);
    }

    [Fact]
    public async Task Hook_ObservesCompletedToolResult()
    {
        // The result is only set once `next()` runs the underlying tool, so the middleware
        // must await before classifying — otherwise it would always observe a null result.
        var counter = new SkillRunnerToolFailureCounter();
        var hook = new NyxIdProxyToolFailureCountingMiddleware(counter);
        var ctx = BuildContext("nyxid_proxy", result: """{"error":true}""");

        await hook.OnToolExecuteEndAsync(ctx, CancellationToken.None);

        counter.FailureCount.Should().Be(1);
    }

    [Fact]
    public void Middleware_ExtractsFailureSampleFromArgumentsAndResult()
    {
        var sample = NyxIdProxyToolFailureCountingMiddleware.ExtractFailureSample(
            """{"slug":"api-github","method":"GET","path":"/repos/ChronoAI/ChronoAIProject/issues?state=open&per_page=100&api_key=secret"}""",
            """{"error":true,"status":404,"body":"{\"message\":\"Not Found\"}"}""");

        sample.ToDiagnosticString()
            .Should().Be("api-github GET /repos/ChronoAI/ChronoAIProject/issues?state=open&per_page=100&api_key=<redacted> -> HTTP 404: Not Found");
    }

    // ─── Policy ───

    [Fact]
    public void Policy_AllFailures_Throws()
    {
        // (a) all-fail case from the issue's acceptance criteria. The thrown
        // InvalidOperationException is what HandleTriggerAsync catches and converts into
        // SkillRunnerExecutionFailedEvent (after the retry budget is exhausted), so
        // /agent-status reports a meaningful error_count and last_error.
        var act = () => SkillRunnerGAgent.EnsureToolStatusAllowsCompletion(
            failureCount: 3, successCount: 0, requiresNyxidProxySuccess: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*数据源请求全部失败*nyxid_proxy 3 次*");
    }

    [Fact]
    public void Policy_AllFailures_IncludesLatestFailureDiagnostic()
    {
        var sample = new SkillRunnerToolFailureSample(
            "api-github",
            "GET",
            "/repos/ChronoAI/ChronoAIProject/issues?state=open&per_page=100",
            404,
            "Not Found");

        var act = () => SkillRunnerGAgent.EnsureToolStatusAllowsCompletion(
            failureCount: 2,
            successCount: 0,
            requiresNyxidProxySuccess: false,
            latestFailure: sample);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*最近失败：api-github GET /repos/ChronoAI/ChronoAIProject/issues?state=open&per_page=100 -> HTTP 404: Not Found*")
            .WithMessage("*Ornn skill*目标服务、仓库、组织或 API 路径写错*");
    }

    [Fact]
    public void Policy_MixedSuccessAndFailure_Allows()
    {
        // (b) mixed case: partial data is more useful than a blanket failure. The
        // prompt-layer §9 Source health footer surfaces which queries failed; the runner
        // simply lets the run complete normally.
        var act = () => SkillRunnerGAgent.EnsureToolStatusAllowsCompletion(
            failureCount: 2, successCount: 4, requiresNyxidProxySuccess: false);

        act.Should().NotThrow();
    }

    [Fact]
    public void Policy_GenuinelyEmpty_Allows()
    {
        // (c) genuine empty-day case: every nyxid_proxy call returned 2xx with no matching
        // items, so the runner records the LLM's "No measurable activity" output as a
        // legitimate success.
        var act = () => SkillRunnerGAgent.EnsureToolStatusAllowsCompletion(
            failureCount: 0, successCount: 7, requiresNyxidProxySuccess: false);

        act.Should().NotThrow();
    }

    [Fact]
    public void Policy_NoToolCallsAtAll_FlagOff_Allows()
    {
        // Skills that don't fan out to nyxid_proxy at all (e.g. pure LLM transformations)
        // leave RequiresNyxidProxySuccess false and pass through. The flag-on case below
        // covers the summary path that was flagged in PR #471 review as the remaining
        // hallucinated-report failure mode.
        var act = () => SkillRunnerGAgent.EnsureToolStatusAllowsCompletion(
            failureCount: 0, successCount: 0, requiresNyxidProxySuccess: false);

        act.Should().NotThrow();
    }

    [Fact]
    public void Policy_NoToolCallsAtAll_FlagOn_Throws()
    {
        // Closes the gap left by the original safety net (PR #471 review): when a
        // fetch-and-summarize skill like summary completes with zero successful
        // nyxid_proxy calls, the LLM produced text from prior context — the original
        // #439 symptom (52 commits in 24h reported as "No meaningful public GitHub
        // activity") with no tool errors to count.
        var act = () => SkillRunnerGAgent.EnsureToolStatusAllowsCompletion(
            failureCount: 0, successCount: 0, requiresNyxidProxySuccess: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*要求至少成功读取一次数据源*没有任何成功的 nyxid_proxy 调用*");
    }

    [Fact]
    public void Policy_MixedSuccessAndFailure_FlagOn_Allows()
    {
        // Flag is only consulted when successCount == 0. Any successful nyxid_proxy call
        // means the LLM did fetch real source data, so partial-data behavior matches the
        // flag-off mixed case (delegated to prompt §9).
        var act = () => SkillRunnerGAgent.EnsureToolStatusAllowsCompletion(
            failureCount: 2, successCount: 4, requiresNyxidProxySuccess: true);

        act.Should().NotThrow();
    }

    [Fact]
    public void Policy_GenuinelyEmpty_FlagOn_Allows()
    {
        // Genuine empty-day stays a success regardless of the flag — every nyxid_proxy
        // call returned 2xx with no matching items, the LLM did fetch source data, and
        // "No measurable activity" is the correct prompt fallback.
        var act = () => SkillRunnerGAgent.EnsureToolStatusAllowsCompletion(
            failureCount: 0, successCount: 7, requiresNyxidProxySuccess: true);

        act.Should().NotThrow();
    }

    // ─── Legacy actor default ───

    [Theory]
    [InlineData("summary")]
    [InlineData("future_pure_llm")]
    [InlineData("")]
    [InlineData(null)]
    public void RequiresProxySuccessByTemplate_AlwaysReturnsFalse(string? templateName)
    {
        // Issue #598: with /summary migrated to Ornn, no template name carries an auto-opt-in
        // semantic anymore. Skills now own their own success criteria; the legacy
        // template-name-derived default is reserved for future templates and currently
        // returns false for every input.
        SkillRunnerGAgent.RequiresProxySuccessByTemplate(templateName).Should().BeFalse();
    }

    [Fact]
    public void Policy_AllFailures_FlagOn_AllFailMessageWins()
    {
        // When both the all-fail and never-called branches would fire (failureCount > 0,
        // successCount == 0, flag = true), the all-fail message is more actionable — it
        // names the count of failed tool calls so the operator knows where to look. Pin
        // that ordering so a future refactor doesn't accidentally swap them.
        var act = () => SkillRunnerGAgent.EnsureToolStatusAllowsCompletion(
            failureCount: 3, successCount: 0, requiresNyxidProxySuccess: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*数据源请求全部失败*nyxid_proxy 3 次*");
    }

    // ─── End-to-end wiring ───

    [Fact]
    public async Task Wiring_HookRegisteredOnAgent_FeedsAgentCounter()
    {
        // The previous wiring assertion was tautological (compared the test-only accessor
        // to itself). Drive the hook that AIGAgentBase actually registered for this
        // agent and verify the same counter the runner reads in
        // EnsureToolStatusAllowsCompletion gets incremented. This catches regressions in
        // BuildAdditionalHooks where the counter could be detached from the hook that
        // the chat loop runs.
        var agent = new SkillRunnerGAgent();

        var registeredField = typeof(AIGAgentBase<SkillRunnerState>).GetField(
            "_additionalHooks", BindingFlags.Instance | BindingFlags.NonPublic);
        registeredField.Should().NotBeNull();
        var registered = (IReadOnlyList<IAIGAgentExecutionHook>?)registeredField!.GetValue(agent);
        registered.Should().NotBeNull();

        var registeredCounting = registered!
            .OfType<NyxIdProxyToolFailureCountingMiddleware>()
            .Should().ContainSingle("the runner appends exactly one counting hook")
            .Subject;

        var ctx = BuildContext("nyxid_proxy", result: """{"error":true,"status":502}""");
        await registeredCounting.OnToolExecuteEndAsync(ctx, CancellationToken.None);

        agent.ToolFailureCounterForTesting.FailureCount.Should().Be(1);
    }

    [Fact]
    public void Wiring_PreservesCallerInjectedHook()
    {
        var injected = new RecordingHook();
        var agent = new SkillRunnerGAgent(additionalHooks: new IAIGAgentExecutionHook[] { injected });

        var registeredField = typeof(AIGAgentBase<SkillRunnerState>).GetField(
            "_additionalHooks", BindingFlags.Instance | BindingFlags.NonPublic);
        var registered = (IReadOnlyList<IAIGAgentExecutionHook>?)registeredField!.GetValue(agent);

        registered.Should().Contain(injected, "caller-injected hooks must survive");
        registered.Should().ContainSingle(hook => hook is NyxIdProxyToolFailureCountingMiddleware);
    }

    private static AIGAgentExecutionHookContext BuildContext(
        string toolName,
        string? result,
        string argumentsJson = "{}") => new()
    {
        ToolName = toolName,
        ToolCallId = "call-1",
        ToolArguments = argumentsJson,
        ToolResult = result,
    };

    private sealed class RecordingHook : IAIGAgentExecutionHook
    {
        public string Name => nameof(RecordingHook);
        public int Priority => 0;
    }
}
