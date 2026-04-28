using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Pin the structural error marker that <see cref="NyxIdProxyTool"/> injects on every
/// JSON-object response. Issue #439: without this marker the SkillRunner can't tell a
/// transient proxy failure apart from a genuinely empty 2xx response, and a fake-success
/// daily report ends up in the user's Feishu chat.
/// </summary>
public class NyxIdProxyToolStatusAnnotationTests
{
    [Fact]
    public void AnnotateWithToolStatus_NyxIdNon2xxEnvelope_MarksError()
    {
        // Production failure mode flagged in the issue: NyxIdApiClient.SendAsync wraps any
        // upstream non-2xx as `{"error": true, "status": <code>, "body": "..."}`. The marker
        // must classify this as error so the LLM and the runner-side counter don't fold it
        // into the empty-day fallback.
        var input = """{"error":true,"status":401,"body":"{\"message\":\"Bad credentials\"}"}""";

        var annotated = NyxIdProxyTool.AnnotateWithToolStatus(input);

        using var doc = JsonDocument.Parse(annotated);
        doc.RootElement.GetProperty(NyxIdProxyTool.ToolStatusFieldName).GetString()
            .Should().Be(NyxIdProxyTool.ToolStatusError);
        // Original payload must be preserved in full — consumers that already parsed
        // `error/status/body` keep working.
        doc.RootElement.GetProperty("error").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(401);
    }

    [Fact]
    public void AnnotateWithToolStatus_NyxIdApprovalEnvelope_MarksError()
    {
        // NyxID approval gate (codes 7000/7001) blocks the proxy until the user approves.
        // The data was not retrieved, so the call counts as an error from the runner's
        // perspective (matching the existing IsApprovalError detection).
        var input = """{"code":7000,"approval_request_id":"req-1","message":"approval_required"}""";

        var annotated = NyxIdProxyTool.AnnotateWithToolStatus(input);

        using var doc = JsonDocument.Parse(annotated);
        doc.RootElement.GetProperty(NyxIdProxyTool.ToolStatusFieldName).GetString()
            .Should().Be(NyxIdProxyTool.ToolStatusError);
    }

    [Fact]
    public void AnnotateWithToolStatus_LarkBusinessErrorEnvelope_MarksError()
    {
        // Lark returns business errors as HTTP 200 with `code != 0`. The marker must catch
        // these too so a Lark proxy call that came back rejected doesn't masquerade as a
        // successful tool invocation.
        var input = """{"code":230002,"msg":"Bot is not in the chat"}""";

        var annotated = NyxIdProxyTool.AnnotateWithToolStatus(input);

        using var doc = JsonDocument.Parse(annotated);
        doc.RootElement.GetProperty(NyxIdProxyTool.ToolStatusFieldName).GetString()
            .Should().Be(NyxIdProxyTool.ToolStatusError);
    }

    [Fact]
    public void AnnotateWithToolStatus_GitHubSuccessShape_MarksOk()
    {
        // GitHub /search/* success: top-level object with `total_count` and `items`. No
        // `error` field, no `code` field — must classify as ok so the runner counts the
        // call as a successful data fetch.
        var input = """{"total_count":52,"incomplete_results":false,"items":[{"sha":"abc"}]}""";

        var annotated = NyxIdProxyTool.AnnotateWithToolStatus(input);

        using var doc = JsonDocument.Parse(annotated);
        doc.RootElement.GetProperty(NyxIdProxyTool.ToolStatusFieldName).GetString()
            .Should().Be(NyxIdProxyTool.ToolStatusOk);
        doc.RootElement.GetProperty("total_count").GetInt32().Should().Be(52);
    }

    [Fact]
    public void AnnotateWithToolStatus_LarkBusinessSuccessCode_MarksOk()
    {
        // Lark business success (`code = 0`). Must NOT be classified as error just because
        // a `code` field is present — the value matters.
        var input = """{"code":0,"msg":"success","data":{"message_id":"om_1"}}""";

        var annotated = NyxIdProxyTool.AnnotateWithToolStatus(input);

        using var doc = JsonDocument.Parse(annotated);
        doc.RootElement.GetProperty(NyxIdProxyTool.ToolStatusFieldName).GetString()
            .Should().Be(NyxIdProxyTool.ToolStatusOk);
    }

    [Fact]
    public void AnnotateWithToolStatus_ErrorFieldExplicitFalse_MarksOk()
    {
        // Edge case: a body that explicitly carries `"error": false` (e.g., a Lark response
        // that defensively echoes the error flag). Must NOT trip the truthy-error check.
        var input = """{"error":false,"data":{"id":"x"}}""";

        var annotated = NyxIdProxyTool.AnnotateWithToolStatus(input);

        using var doc = JsonDocument.Parse(annotated);
        doc.RootElement.GetProperty(NyxIdProxyTool.ToolStatusFieldName).GetString()
            .Should().Be(NyxIdProxyTool.ToolStatusOk);
    }

    [Fact]
    public void AnnotateWithToolStatus_NonObjectRoot_ReturnsUnchanged()
    {
        // Discovery responses are JSON arrays, not objects. The annotator must leave them
        // alone so existing callers (DiscoverMergedServicesAsync, ParseServiceSlugs) keep
        // parsing the array shape. Same protection covers raw text bodies.
        var input = """[{"slug":"api-github"}]""";

        var annotated = NyxIdProxyTool.AnnotateWithToolStatus(input);

        annotated.Should().Be(input);
    }

    [Fact]
    public void AnnotateWithToolStatus_NonJsonBody_ReturnsUnchanged()
    {
        var input = "plain text body";

        var annotated = NyxIdProxyTool.AnnotateWithToolStatus(input);

        annotated.Should().Be(input);
    }

    [Fact]
    public void AnnotateWithToolStatus_EmptyBody_ReturnsUnchanged()
    {
        NyxIdProxyTool.AnnotateWithToolStatus(string.Empty).Should().Be(string.Empty);
        NyxIdProxyTool.AnnotateWithToolStatus(null).Should().Be(string.Empty);
    }

    [Fact]
    public void AnnotateWithToolStatus_AlreadyAnnotated_DoesNotDuplicateMarker()
    {
        // Idempotent: a body that already carries the marker (e.g., re-classified by an
        // earlier middleware pass) must not get a second marker injected, which would
        // change the JSON shape and confuse downstream parsers.
        var input = $$"""{"{{NyxIdProxyTool.ToolStatusFieldName}}":"ok","total_count":1}""";

        var annotated = NyxIdProxyTool.AnnotateWithToolStatus(input);

        annotated.Should().Be(input);
    }

    [Fact]
    public void AnnotateWithToolStatus_MarkerAppearsBeforeOriginalFields()
    {
        // Surface the status before any payload field so consumers eyeballing tool output
        // (or LLMs streaming partial JSON) see the classification first.
        var input = """{"total_count":1,"items":[]}""";

        var annotated = NyxIdProxyTool.AnnotateWithToolStatus(input);

        annotated.Should().StartWith("{\"" + NyxIdProxyTool.ToolStatusFieldName + "\":\"ok\"");
    }
}
