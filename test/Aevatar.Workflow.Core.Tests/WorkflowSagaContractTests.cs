using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowSagaContractTests
{
    [Fact]
    public void WorkflowRunState_ShouldExposeCompensationLedgerContract()
    {
        var state = new WorkflowRunState
        {
            CompensationCursor = 1,
            SagaStatus = WorkflowSagaStatus.Compensating,
        };
        state.CompensableLedger.Add(new CompletedStepLedgerEntry
        {
            StepId = "create_order",
            CompensationStepId = "cancel_order",
            IdempotencyKey = "run-1:create_order",
            CapturedOutput = """{"orderId":"order-1"}""",
            LedgerStatus = CompensableLedgerEntryStatus.Confirmed,
        });

        state.CompensableLedger.Should().ContainSingle();
        state.CompensableLedger[0].StepId.Should().Be("create_order");
        state.CompensableLedger[0].CompensationStepId.Should().Be("cancel_order");
        state.CompensableLedger[0].IdempotencyKey.Should().Be("run-1:create_order");
        state.CompensableLedger[0].CapturedOutput.Should().Be("""{"orderId":"order-1"}""");
        state.CompensableLedger[0].LedgerStatus.Should().Be(CompensableLedgerEntryStatus.Confirmed);
        state.CompensationCursor.Should().Be(1);
        state.SagaStatus.Should().Be(WorkflowSagaStatus.Compensating);
    }

    [Fact]
    public void WorkflowRunState_ShouldExposeCompensationDeadLetterContract()
    {
        var state = new WorkflowRunState
        {
            SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
            DeadLetterFailedCompensationStepId = "refund_payment",
            DeadLetterRemainingUncompensated = 2,
            DeadLetterError = "refund failed",
        };

        state.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        state.DeadLetterFailedCompensationStepId.Should().Be("refund_payment");
        state.DeadLetterRemainingUncompensated.Should().Be(2);
        state.DeadLetterError.Should().Be("refund failed");
    }

    [Fact]
    public void WorkflowExecutionMessages_ShouldExposeCompensationEventContracts()
    {
        AssertField<StepCompletedEvent>("failure_outcome", 15);
        AssertField<StepCompletedEvent>("output_source_step_id", 22);
        AssertField<StepCompletedEvent>("output_source_execution_id", 23);
        AssertField<StepCompletedEvent>("output_source_value_id", 24);
        AssertRoundTrip(new StepCompletedEvent
        {
            RunId = "run-1",
            StepId = "create_order",
            Success = false,
            Error = "timeout",
            FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain,
            OutputProvenance = WorkflowStepOutputProvenance.ReferencedStepOutput,
            OutputSourceStepId = "source-step",
            OutputSourceExecutionId = "source-execution",
            OutputSourceValueId = "value-00000000000000000001",
        });

        AssertField<CompensableStepDispatchedEvent>("run_id", 1);
        AssertField<CompensableStepDispatchedEvent>("step_id", 2);
        AssertField<CompensableStepDispatchedEvent>("compensation_step_id", 3);
        AssertField<CompensableStepDispatchedEvent>("idempotency_key", 4);
        AssertField<CompensableStepDispatchedEvent>("dispatched_at_unix_ms", 5);
        AssertRoundTrip(new CompensableStepDispatchedEvent
        {
            RunId = "run-1",
            StepId = "create_order",
            CompensationStepId = "cancel_order",
            IdempotencyKey = "run-1:create_order:1",
            DispatchedAtUnixMs = 123456789,
        });

        AssertField<CompensationRequestEvent>("run_id", 1);
        AssertField<CompensationRequestEvent>("failed_step_id", 2);
        AssertField<CompensationRequestEvent>("compensation_step_id", 3);
        AssertField<CompensationRequestEvent>("idempotency_key", 4);
        AssertField<CompensationRequestEvent>("captured_output", 5);
        AssertField<CompensationRequestEvent>("execution_id", 6);
        AssertRoundTrip(new CompensationRequestEvent
        {
            RunId = "run-1",
            FailedStepId = "charge_payment",
            CompensationStepId = "cancel_order",
            IdempotencyKey = "run-1:create_order",
            CapturedOutput = """{"orderId":"order-1"}""",
            ExecutionId = "compensate-1",
        });

        AssertField<CompensationStepCompletedEvent>("run_id", 1);
        AssertField<CompensationStepCompletedEvent>("compensation_step_id", 2);
        AssertField<CompensationStepCompletedEvent>("success", 3);
        AssertField<CompensationStepCompletedEvent>("error", 4);
        AssertField<CompensationStepCompletedEvent>("execution_id", 5);
        AssertRoundTrip(new CompensationStepCompletedEvent
        {
            RunId = "run-1",
            CompensationStepId = "cancel_order",
            Success = true,
            Error = "",
            ExecutionId = "compensate-1",
        });

        AssertField<WorkflowCompensationCompletedEvent>("run_id", 1);
        AssertField<WorkflowCompensationCompletedEvent>("compensated_steps", 2);
        AssertRoundTrip(new WorkflowCompensationCompletedEvent
        {
            RunId = "run-1",
            CompensatedSteps = 1,
        });

        AssertField<WorkflowCompensationFailedEvent>("run_id", 1);
        AssertField<WorkflowCompensationFailedEvent>("failed_compensation_step_id", 2);
        AssertField<WorkflowCompensationFailedEvent>("remaining_uncompensated", 3);
        AssertField<WorkflowCompensationFailedEvent>("error", 4);
        AssertRoundTrip(new WorkflowCompensationFailedEvent
        {
            RunId = "run-1",
            FailedCompensationStepId = "cancel_order",
            RemainingUncompensated = 1,
            Error = "failed",
        });

        AssertField<WorkflowCompensationPhaseDeadlineFiredEvent>("run_id", 1);
        AssertRoundTrip(new WorkflowCompensationPhaseDeadlineFiredEvent
        {
            RunId = "run-1",
        });

        AssertField<SubWorkflowInvocationCompletedEvent>("compensated", 6);
        AssertRoundTrip(new SubWorkflowInvocationCompletedEvent
        {
            InvocationId = "invoke-1",
            ChildRunId = "child-run-1",
            Success = false,
            Output = string.Empty,
            Error = "failed after compensation",
            Compensated = true,
        });
    }

    [Fact]
    public void WorkflowSagaEnums_ShouldPreserveFieldNumbersAndRoundTrip()
    {
        ((int)WorkflowStepFailureOutcome.Unspecified).Should().Be(0);
        ((int)WorkflowStepFailureOutcome.CalleeConfirmed).Should().Be(1);
        ((int)WorkflowStepFailureOutcome.OutcomeUncertain).Should().Be(2);
        ((int)CompensableLedgerEntryStatus.Unspecified).Should().Be(0);
        ((int)CompensableLedgerEntryStatus.Confirmed).Should().Be(1);
        ((int)CompensableLedgerEntryStatus.Provisional).Should().Be(2);

        AssertField<CompletedStepLedgerEntry>("step_id", 1);
        AssertField<CompletedStepLedgerEntry>("compensation_step_id", 2);
        AssertField<CompletedStepLedgerEntry>("idempotency_key", 3);
        AssertField<CompletedStepLedgerEntry>("captured_output", 4);
        AssertFieldMissing<CompletedStepLedgerEntry>("committed_at_unix_ms");
        AssertReserved<CompletedStepLedgerEntry>("committed_at_unix_ms", 5);
        AssertField<CompletedStepLedgerEntry>("ledger_status", 6);
        AssertField<WorkflowRunState>("compensable_ledger", 25);
        AssertField<WorkflowRunState>("compensation_cursor", 26);
        AssertField<WorkflowRunState>("saga_status", 27);
        AssertField<WorkflowExecutionKernelState>("compensation_phase_deadline_lease", 17);
        AssertField<WorkflowExecutionKernelState>("compensation_phase_deadline_callback_id", 18);
        AssertField<WorkflowExecutionKernelState>("pending_compensation_outcome", 22);
        AssertField<WorkflowRunState>("compensation_origin_failed_step_id", 32);
        AssertField<WorkflowRunState>("terminal_workflow_completion_recorded", 33);
        AssertField<WorkflowRunState>("pending_compensation_completion", 63);
        AssertRoundTrip(new CompletedStepLedgerEntry
        {
            StepId = "create_order",
            CompensationStepId = "cancel_order",
            IdempotencyKey = "run-1:create_order:1",
            CapturedOutput = "{}",
            LedgerStatus = CompensableLedgerEntryStatus.Provisional,
        });
    }

    private static void AssertField<TMessage>(string name, int number)
        where TMessage : IMessage<TMessage>, new()
    {
        var field = new TMessage().Descriptor.FindFieldByName(name);

        field.Should().NotBeNull();
        field!.FieldNumber.Should().Be(number);
    }

    private static void AssertFieldMissing<TMessage>(string name)
        where TMessage : IMessage<TMessage>, new()
    {
        new TMessage().Descriptor.FindFieldByName(name).Should().BeNull();
    }

    private static void AssertReserved<TMessage>(string name, int number)
        where TMessage : IMessage<TMessage>, new()
    {
        var messageDescriptor = new TMessage().Descriptor;
        var file = FileDescriptorProto.Parser.ParseFrom(messageDescriptor.File.SerializedData);
        var descriptor = file.MessageType.Single(message => message.Name == messageDescriptor.Name);

        descriptor.ReservedName.Should().Contain(name);
        descriptor.ReservedRange.Should().Contain(range =>
            range.Start <= number &&
            number < range.End);
    }

    private static void AssertRoundTrip<TMessage>(TMessage message)
        where TMessage : IMessage<TMessage>, new()
    {
        var clone = new TMessage().Descriptor.Parser.ParseFrom(message.ToByteArray());

        clone.Should().BeEquivalentTo(message);
    }
}
