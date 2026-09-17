using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class WorkflowValueLifecycleRedactionTests
{
    [Fact]
    public async Task BeforePublishAsync_ShouldRedactLifecycleDigestsInEventAndFullStateRoot()
    {
        var kernel = CreateKernelState();
        var published = new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = "evt-lifecycle",
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Version = 2,
                AgentId = "run-lifecycle",
                EventType = nameof(WorkflowExecutionStateUpsertedEvent),
                EventData = Any.Pack(new WorkflowExecutionStateUpsertedEvent
                {
                    ScopeKey = WorkflowExecutionKernel.ModuleStateKey,
                    State = Any.Pack(kernel),
                }),
            },
            StateRoot = Any.Pack(new WorkflowRunState
            {
                RunId = "run-lifecycle",
                ExecutionStates =
                {
                    [WorkflowExecutionKernel.ModuleStateKey] = Any.Pack(kernel),
                },
            }),
        };

        await new WorkflowRunCommittedStateRedactionHook().BeforePublishAsync(
            new CommittedStatePublicationContext
            {
                ActorId = "run-lifecycle",
                ActorType = typeof(WorkflowRunGAgent),
                Published = published,
            },
            CancellationToken.None);

        var eventKernel = published.StateEvent.EventData
            .Unpack<WorkflowExecutionStateUpsertedEvent>()
            .State.Unpack<WorkflowExecutionKernelState>();
        var rootKernel = published.StateRoot.Unpack<WorkflowRunState>()
            .ExecutionStates[WorkflowExecutionKernel.ModuleStateKey]
            .Unpack<WorkflowExecutionKernelState>();
        AssertRedacted(eventKernel);
        AssertRedacted(rootKernel);

        kernel.NormalizedValues!.CanonicalValues[ValueId].Released.Digest.Sha256.Should().HaveCount(32);
        kernel.NormalizedValues.CompletedSteps["producer"].OutputDigest.Should().NotBeNull();
    }

    private static void AssertRedacted(WorkflowExecutionKernelState kernel)
    {
        var normalized = kernel.NormalizedValues!;
        var canonical = normalized.CanonicalValues[ValueId];
        canonical.Value.Should().BeEmpty();
        canonical.Released.ReleasedAfterStepId.Should().Be("reduce");
        canonical.Released.ReleasedAfterExecutionId.Should().Be("reduce-execution");
        canonical.Released.Digest.Redacted.Should().BeTrue();
        canonical.Released.Digest.Sha256.Should().BeEmpty();
        canonical.Released.Digest.Utf8Size.Should().Be(0);
        normalized.ReleasedBindings["raw_pages"].Digest.Redacted.Should().BeTrue();
        normalized.CompletedSteps["producer"].OutputDigest.Should().BeNull();
        normalized.AcceptedCompletions["producer\0producer-execution"].OutputDigest.Should().BeNull();
    }

    private static WorkflowExecutionKernelState CreateKernelState()
    {
        var digest = new WorkflowValueDigest
        {
            Sha256 = ByteString.CopyFrom(Enumerable.Repeat((byte)0x2a, 32).ToArray()),
            Utf8Size = 14,
        };
        var released = new WorkflowReleasedValueTombstone
        {
            Digest = digest.Clone(),
            ReleasedAfterStepId = "reduce",
            ReleasedAfterExecutionId = "reduce-execution",
        };
        var completion = new WorkflowCompletedStepState
        {
            StepId = "producer",
            ExecutionId = "producer-execution",
            OutputValueId = ValueId,
            OutputDigest = digest.Clone(),
        };
        return new WorkflowExecutionKernelState
        {
            NormalizedValues = new WorkflowNormalizedExecutionValuesState
            {
                CanonicalValues =
                {
                    [ValueId] = new WorkflowCanonicalValueState
                    {
                        ValueId = ValueId,
                        Value = "must-not-publish",
                        SourceKind = WorkflowCanonicalValueSourceKind.StepOutput,
                        ProducerStepId = "producer",
                        ProducerExecutionId = "producer-execution",
                        Released = released.Clone(),
                    },
                },
                ReleasedBindings = { ["raw_pages"] = released.Clone() },
                CompletedSteps = { ["producer"] = completion.Clone() },
                AcceptedCompletions =
                {
                    ["producer\0producer-execution"] = completion.Clone(),
                },
            },
        };
    }

    private const string ValueId = "value-00000000000000000001";
}
