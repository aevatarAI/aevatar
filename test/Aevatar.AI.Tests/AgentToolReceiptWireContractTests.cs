using Aevatar.AI.Abstractions;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class AgentToolReceiptWireContractTests
{
    [Fact]
    public void AgentToolReceipt_ShouldUseCanonicalWireContract()
    {
        ((int)AgentToolReceiptStatus.Unspecified).Should().Be(0);
        ((int)AgentToolReceiptStatus.Success).Should().Be(1);
        ((int)AgentToolReceiptStatus.ApprovalRequired).Should().Be(2);
        ((int)AgentToolReceiptStatus.Denied).Should().Be(3);
        ((int)AgentToolReceiptStatus.Error).Should().Be(4);
        ((int)AgentToolReceiptApprovalMode.Unspecified).Should().Be(0);
        ((int)AgentToolReceiptApprovalMode.NeverRequire).Should().Be(1);
        ((int)AgentToolReceiptApprovalMode.AlwaysRequire).Should().Be(2);
        ((int)AgentToolReceiptApprovalMode.Auto).Should().Be(3);
        ((int)AgentToolReceiptMutationStage.Unspecified).Should().Be(0);
        ((int)AgentToolReceiptMutationStage.Accepted).Should().Be(1);
        ((int)AgentToolReceiptMutationStage.ReadModelObserved).Should().Be(2);
        ((int)AgentToolFailureOutcome.Unspecified).Should().Be(0);
        ((int)AgentToolFailureOutcome.CalleeConfirmed).Should().Be(1);
        ((int)AgentToolFailureOutcome.OutcomeUncertain).Should().Be(2);
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
                (17, "authorization_required"),
                (18, "effect"),
                (19, "provider_resource_id"),
                (20, "nyx_id_approval_decision_mode"),
                (21, "mutation_stage"),
                (22, "nyx_id_approval_terminal_outcome"),
                (23, "exact_service_approval"),
                (24, "failure_outcome"));
        var receipt = new AgentToolReceipt
        {
            CallId = "call-uncertain",
            Status = AgentToolReceiptStatus.Error,
            FailureOutcome = AgentToolFailureOutcome.OutcomeUncertain,
        };
        AgentToolReceipt.Parser.ParseFrom(receipt.ToByteArray()).FailureOutcome.Should()
            .Be(AgentToolFailureOutcome.OutcomeUncertain);
        ((int)NyxIdApprovalTerminalOutcome.Unspecified).Should().Be(0);
        ((int)NyxIdApprovalTerminalOutcome.Rejected).Should().Be(1);
        ((int)NyxIdApprovalTerminalOutcome.Expired).Should().Be(2);
        ((int)NyxIdApprovalTerminalOutcome.TimedOut).Should().Be(3);
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
            .Contain((11, "tool_receipts"));
        RoleChatSessionState.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => (field.FieldNumber, field.Name))
            .Should()
            .Contain((12, "tool_receipts"));
    }
}
