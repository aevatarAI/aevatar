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
            SagaStatus = "compensating",
        };
        state.CompensableLedger.Add(new CompletedStepLedgerEntry
        {
            StepId = "create_order",
            CompensationStepId = "cancel_order",
            IdempotencyKey = "run-1:create_order",
            CapturedOutput = """{"orderId":"order-1"}""",
            CommittedAtUnixMs = 123456789,
        });

        state.CompensableLedger.Should().ContainSingle();
        state.CompensableLedger[0].StepId.Should().Be("create_order");
        state.CompensableLedger[0].CompensationStepId.Should().Be("cancel_order");
        state.CompensableLedger[0].IdempotencyKey.Should().Be("run-1:create_order");
        state.CompensableLedger[0].CapturedOutput.Should().Be("""{"orderId":"order-1"}""");
        state.CompensableLedger[0].CommittedAtUnixMs.Should().Be(123456789);
        state.CompensationCursor.Should().Be(1);
        state.SagaStatus.Should().Be("compensating");
    }

    [Fact]
    public void WorkflowRunState_ShouldExposeCompensationDeadLetterContract()
    {
        var state = new WorkflowRunState
        {
            SagaStatus = "compensation_dead_letter",
            DeadLetterFailedCompensationStepId = "refund_payment",
            DeadLetterRemainingUncompensated = 2,
            DeadLetterError = "refund failed",
        };

        state.SagaStatus.Should().Be("compensation_dead_letter");
        state.DeadLetterFailedCompensationStepId.Should().Be("refund_payment");
        state.DeadLetterRemainingUncompensated.Should().Be(2);
        state.DeadLetterError.Should().Be("refund failed");
    }

    [Fact]
    public void WorkflowExecutionMessages_ShouldExposeCompensationEventContracts()
    {
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
    }

    private static void AssertField<TMessage>(string name, int number)
        where TMessage : IMessage<TMessage>, new()
    {
        var field = new TMessage().Descriptor.FindFieldByName(name);

        field.Should().NotBeNull();
        field!.FieldNumber.Should().Be(number);
    }

    private static void AssertRoundTrip<TMessage>(TMessage message)
        where TMessage : IMessage<TMessage>, new()
    {
        var clone = new TMessage().Descriptor.Parser.ParseFrom(message.ToByteArray());

        clone.Should().BeEquivalentTo(message);
    }
}
