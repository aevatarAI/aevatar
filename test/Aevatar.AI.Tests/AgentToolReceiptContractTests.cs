using Aevatar.AI.Abstractions;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class AgentToolReceiptContractTests
{
    [Fact]
    public void AgentToolReceipt_ShouldUseCanonicalWireContract()
    {
        ((int)AgentToolReceiptStatus.Unspecified).Should().Be(0);
        ((int)AgentToolReceiptStatus.Success).Should().Be(1);
        ((int)AgentToolReceiptStatus.ApprovalRequired).Should().Be(2);
        ((int)AgentToolReceiptStatus.Denied).Should().Be(3);
        ((int)AgentToolReceiptStatus.Error).Should().Be(4);
        ((int)AgentToolReceiptStatus.AuthorizationRequired).Should().Be(5);

        ((int)AgentToolReceiptApprovalMode.Unspecified).Should().Be(0);
        ((int)AgentToolReceiptApprovalMode.NeverRequire).Should().Be(1);
        ((int)AgentToolReceiptApprovalMode.AlwaysRequire).Should().Be(2);
        ((int)AgentToolReceiptApprovalMode.Auto).Should().Be(3);
        AgentToolReceipt.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => (field.FieldNumber, field.Name))
            .Should()
            .Equal(
                (1, "call_id"),
                (2, "tool_name"),
                (3, "status"),
                (4, "approval_mode"),
                (5, "is_destructive"),
                (6, "side_effect_kind"),
                (7, "subject_kind"),
                (8, "subject_id"),
                (9, "subject_version"),
                (10, "subject_hash"),
                (11, "approval_request_id"),
                (12, "error_code"),
                (13, "error_message"),
                (14, "result_json"),
                (15, "managed_workflow_handoff"),
                (16, "workflow_run_delivery"),
                (17, "authorization_required"));

        AgentToolReceipt.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => field.Name)
            .Should()
            .NotContain(["observed_at_unix_ms", "subject_name", "is_read_only"]);

        ToolResultEvent.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => (field.FieldNumber, field.Name))
            .Should()
            .Contain((5, "receipt"))
            .And.NotContain((5, "tool_name"));
        ToolResultEvent.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => field.Name)
            .Should()
            .NotContain("tool_name");

        RoleChatSessionCompletedEvent.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => (field.FieldNumber, field.Name))
            .Should()
            .Contain((11, "tool_receipts"))
            .And.Contain((16, "terminal_time"));
        RoleChatSessionState.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => (field.FieldNumber, field.Name))
            .Should()
            .Contain((12, "tool_receipts"))
            .And.Contain((17, "terminal_time"));

        ToolApprovalDecisionEvent.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => (field.FieldNumber, field.Name))
            .Should()
            .Equal(
                (1, "request_id"),
                (3, "approved"),
                (4, "reason"),
                (5, "continuation_turn_id"));
        ToolApprovalDecisionEvent.Descriptor.FindFieldByNumber(2).Should().BeNull();
    }
}
