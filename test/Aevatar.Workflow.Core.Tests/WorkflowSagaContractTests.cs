using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using FluentAssertions;

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
        new CompensationRequestEvent
        {
            RunId = "run-1",
            FailedStepId = "charge_payment",
            CompensationStepId = "cancel_order",
            IdempotencyKey = "run-1:create_order",
            CapturedOutput = """{"orderId":"order-1"}""",
        }.Should().NotBeNull();

        new CompensationStepCompletedEvent
        {
            RunId = "run-1",
            CompensationStepId = "cancel_order",
            Success = true,
            Error = "",
        }.Should().NotBeNull();

        new WorkflowCompensationCompletedEvent
        {
            RunId = "run-1",
            CompensatedSteps = 1,
        }.Should().NotBeNull();

        new WorkflowCompensationFailedEvent
        {
            RunId = "run-1",
            FailedCompensationStepId = "cancel_order",
            RemainingUncompensated = 1,
            Error = "failed",
        }.Should().NotBeNull();
    }
}
