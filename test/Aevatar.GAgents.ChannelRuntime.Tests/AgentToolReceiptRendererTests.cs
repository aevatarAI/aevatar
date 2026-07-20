using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentToolReceiptRendererTests
{
    [Fact]
    public void Render_WhenNoReceipts_ShouldReturnEmptyEvenIfToolArgsOrProseClaimCompletion()
    {
        var renderer = new AgentToolReceiptRenderer();
        var toolCalls = new[]
        {
            new AgentRunToolCall
            {
                Id = "call-1",
                Name = "ornn_publish_skill",
                ArgumentsJson = """{"name":"claimed-skill","markdown":"已提交 / approval required"}""",
            },
        };

        renderer.Render([], toolCalls).Should().BeEmpty();
    }

    [Fact]
    public void Render_WhenSuccess_ShouldRenderNothingToUserVisibleText()
    {
        var renderer = new AgentToolReceiptRenderer();
        var rendered = renderer.Render(
            [
                new AgentToolReceipt
                {
                    CallId = "call-1",
                    ToolName = "ornn_publish_skill",
                    Status = AgentToolReceiptStatus.Success,
                    SideEffectKind = "ornn.publish.skill",
                    SubjectKind = "ornn.skill",
                    SubjectId = "typed-skill",
                    SubjectVersion = "1.0",
                    SubjectHash = "typed-hash",
                    ResultJson = """{"subject_id":"spoofed-skill","status":"approval_required"}""",
                },
            ],
            [
                new AgentRunToolCall
                {
                    Id = "call-1",
                    Name = "ornn_publish_skill",
                    ArgumentsJson = """{"name":"argument-skill"}""",
                },
            ]);

        rendered.Should().BeEmpty();
    }

    [Fact]
    public void Render_ShouldUseTypedReceiptSubjectInsteadOfSpoofedResultJson()
    {
        var renderer = new AgentToolReceiptRenderer();
        var rendered = renderer.Render(
            [
                new AgentToolReceipt
                {
                    CallId = "call-1",
                    ToolName = "ornn_publish_skill",
                    Status = AgentToolReceiptStatus.Denied,
                    SideEffectKind = "ornn.publish.skill",
                    SubjectKind = "ornn.skill",
                    SubjectId = "typed-skill",
                    SubjectVersion = "1.0",
                    SubjectHash = "typed-hash",
                    ResultJson = """{"subject_id":"spoofed-skill","status":"approval_required"}""",
                },
            ],
            [
                new AgentRunToolCall
                {
                    Id = "call-1",
                    Name = "ornn_publish_skill",
                    ArgumentsJson = """{"name":"argument-skill"}""",
                },
            ]);

        rendered.Should().Contain("[tool receipt] Denied: ornn.skill typed-skill");
        rendered.Should().Contain("version=1.0");
        rendered.Should().Contain("hash=typed-hash");
        rendered.Should().NotContain("spoofed-skill");
        rendered.Should().NotContain("argument-skill");
        rendered.Should().NotContain("approval_required");
    }

    [Fact]
    public void Render_WhenSuccessMixedWithFailure_ShouldRenderOnlyNonSuccessLines()
    {
        var renderer = new AgentToolReceiptRenderer();
        var rendered = renderer.Render(
            [
                new AgentToolReceipt
                {
                    CallId = "call-1",
                    ToolName = "aevatar_start_workflow",
                    Status = AgentToolReceiptStatus.Success,
                },
                new AgentToolReceipt
                {
                    CallId = "call-2",
                    ToolName = "dangerous_tool",
                    Status = AgentToolReceiptStatus.Error,
                    ErrorMessage = "blocked",
                },
            ],
            []);

        rendered.Should().NotContain("aevatar_start_workflow");
        rendered.Should().NotContain("Completed");
        rendered.Should().Contain("[tool receipt] Failed: dangerous_tool");
    }

    [Fact]
    public void Render_ShouldAllowApprovalAndArgumentFallbackPresentationOnly()
    {
        var renderer = new AgentToolReceiptRenderer();
        var rendered = renderer.Render(
            [
                new AgentToolReceipt
                {
                    CallId = "call-2",
                    ToolName = "ornn_publish_skill",
                    Status = AgentToolReceiptStatus.ApprovalRequired,
                    ApprovalRequestId = "approval-1",
                },
            ],
            [
                new AgentRunToolCall
                {
                    Id = "call-2",
                    Name = "ornn_publish_skill",
                    ArgumentsJson = """{"name":"draft-skill"}""",
                },
            ]);

        rendered.Should().Contain("[tool receipt] Approval pending: draft-skill");
        rendered.Should().Contain("local_request=approval-1");
    }

    [Theory]
    [InlineData(AgentToolReceiptStatus.Denied, "Denied")]
    [InlineData(AgentToolReceiptStatus.Error, "Failed")]
    public void Render_ShouldRenderTypedNegativeStatuses(AgentToolReceiptStatus status, string expectedLabel)
    {
        var renderer = new AgentToolReceiptRenderer();

        var rendered = renderer.Render(
            [
                new AgentToolReceipt
                {
                    CallId = "call-3",
                    ToolName = "dangerous_tool",
                    Status = status,
                    ErrorMessage = "blocked",
                },
            ],
            []);

        rendered.Should().Contain($"[tool receipt] {expectedLabel}: dangerous_tool");
        rendered.Should().Contain("blocked");
    }
}
