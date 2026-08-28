using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Hooks;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.Workflow.Core.Tests;

/// <summary>
/// R1 (06-20-observatory-run-state-feed): the projection-root run actor must adopt a terminal that
/// belongs to its OWN run when the terminal was executed/relayed by a sub-actor of the same logical run
/// (publisher != self), so the current-state projector gate passes and the summary status advances.
/// A genuine child sub-workflow terminal (different run id) must NOT clobber the parent's state.
/// </summary>
public sealed class WorkflowRunGAgentRelayedTerminalAdoptionTests
{
    private const string ChildActorId = "scope-workflow:wf:run:child-executor";

    [Fact]
    public async Task CompletedScheduledRun_ShouldRevokeDurableCallerCredential()
    {
        var vault = new InMemorySecretVault();
        var stored = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
            "schedule:schedule-1",
            "nyxid::scope-1",
            "caller-token",
            "test",
            DateTimeOffset.UtcNow.AddMinutes(5)));
        var runId = "run-scheduled-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId, secretVault: vault);
        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
            CallerCredential = new WorkflowCallerCredential
            {
                DurableCallerCredential = new DurableCallerCredentialRef
                {
                    Ref = stored.Reference.Ref,
                    Purpose = stored.Reference.Purpose,
                    OwnerScopeKey = stored.Reference.OwnerScopeKey,
                    SubjectId = "nyxid::scope-1",
                    SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
                },
                DurableCredentialCleanupResponsibility =
                    WorkflowCallerCredentialCleanupResponsibility.Borrowed,
            },
        }));
        harness.Agent.State.ExecutionContext.CallerCredential
            .DurableCredentialCleanupResponsibility.Should()
            .Be(WorkflowCallerCredentialCleanupResponsibility.Owner);

        await harness.Agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = runId,
            WorkflowName = "wf_relayed",
            Success = true,
            Output = "done",
        });

        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            stored.Reference.Purpose,
            stored.Reference.OwnerScopeKey,
            "nyxid::scope-1",
            "verify-terminal-cleanup"));
        resolved.Resolved.Should().BeFalse();
        resolved.FailureReason.Should().Be(SecretResolutionFailureReason.Revoked);
    }

    [Fact]
    public async Task CompletedChildRun_ShouldNotRevokeBorrowedDurableCallerCredential()
    {
        var vault = new InMemorySecretVault();
        var stored = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
            "schedule:schedule-parent",
            "nyxid::scope-parent",
            "caller-token",
            "test",
            DateTimeOffset.UtcNow.AddMinutes(5)));
        var runId = "run-child-" + Guid.NewGuid().ToString("N");
        const string parentActorId = "workflow-run:parent";
        var harness = await CreateRunAsync(
            runId,
            secretVault: vault,
            initialLineage: new WorkflowRunLineage
            {
                Availability = WorkflowRunLineageAvailability.Available,
                SubWorkflow = new WorkflowRunSubWorkflowLineage
                {
                    Availability = WorkflowRunLineageAvailability.Available,
                    ParentActorId = parentActorId,
                    ParentRunId = "parent-run",
                    ParentStepId = "call-child",
                    RootRunId = "parent-run",
                    Depth = 1,
                },
            });
        await harness.Agent.HandleEventAsync(EnvelopeFrom(parentActorId, new StartWorkflowEvent
        {
            WorkflowName = "wf_relayed",
            RunId = runId,
            Input = "hello",
            ExecutionContextDelta = new WorkflowRunExecutionContextDelta
            {
                ClearCallerCredential = true,
                CallerCredential = new WorkflowCallerCredential
                {
                    DurableCallerCredential = new DurableCallerCredentialRef
                    {
                        Ref = stored.Reference.Ref,
                        Purpose = stored.Reference.Purpose,
                        OwnerScopeKey = stored.Reference.OwnerScopeKey,
                        SubjectId = "nyxid::scope-parent",
                        SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
                    },
                    DurableCredentialCleanupResponsibility =
                        WorkflowCallerCredentialCleanupResponsibility.Borrowed,
                },
            },
        }));
        harness.Agent.State.ExecutionContext.CallerCredential
            .DurableCredentialCleanupResponsibility.Should()
            .Be(WorkflowCallerCredentialCleanupResponsibility.Borrowed);

        await harness.Agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = runId,
            WorkflowName = "wf_relayed",
            Success = true,
            Output = "done",
        });

        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            stored.Reference.Ref,
            stored.Reference.Purpose,
            stored.Reference.OwnerScopeKey,
            "nyxid::scope-parent",
            "verify-borrowed-credential-retained"));
        resolved.Resolved.Should().BeTrue();
        resolved.Secret.Should().Be("caller-token");
    }

    [Fact]
    public async Task InheritedStartContext_ShouldRequireCommittedParentPublisher()
    {
        const string trustedParentActorId = "workflow-run:trusted-parent";
        var runId = "run-child-start-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(
            runId,
            initialLineage: new WorkflowRunLineage
            {
                Availability = WorkflowRunLineageAvailability.Available,
                SubWorkflow = new WorkflowRunSubWorkflowLineage
                {
                    Availability = WorkflowRunLineageAvailability.Available,
                    ParentActorId = trustedParentActorId,
                    ParentRunId = "parent-run",
                    ParentStepId = "call-child",
                    RootRunId = "parent-run",
                    Depth = 1,
                },
            });
        var start = new StartWorkflowEvent
        {
            WorkflowName = "wf_relayed",
            RunId = runId,
            Input = "hello",
            ExecutionContextDelta = new WorkflowRunExecutionContextDelta
            {
                ClearCallerCredential = true,
                CallerCredential = new WorkflowCallerCredential
                {
                    RuntimeSecretReference = new RuntimeSecretReference
                    {
                        Ref = "borrowed-runtime-secret",
                        Purpose = CredentialSecretPurposes.WorkflowCallerBearerToken,
                        OwnerRunId = "parent-run",
                        OwnerStepId = "workflow.caller",
                    },
                },
            },
        };

        await harness.Agent.HandleEventAsync(EnvelopeFrom("workflow-run:attacker", start.Clone()));

        harness.Publisher.Published.Select(x => x.Event).OfType<StepRequestEvent>()
            .Should().BeEmpty();
        harness.Agent.State.ExecutionContext.CallerCredential.Should().BeNull();

        await harness.Agent.HandleEventAsync(EnvelopeFrom(trustedParentActorId, start.Clone()));

        harness.Publisher.Published.Select(x => x.Event).OfType<StepRequestEvent>()
            .Should().ContainSingle();
        harness.Agent.State.ExecutionContext.CallerCredential.RuntimeSecretReference.Ref
            .Should().Be("borrowed-runtime-secret");
    }

    [Fact]
    public async Task InheritedStartContext_ShouldRejectOwnedDurableCallerCredential()
    {
        const string trustedParentActorId = "workflow-run:trusted-parent";
        var runId = "run-child-owned-credential-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(
            runId,
            initialLineage: new WorkflowRunLineage
            {
                Availability = WorkflowRunLineageAvailability.Available,
                SubWorkflow = new WorkflowRunSubWorkflowLineage
                {
                    Availability = WorkflowRunLineageAvailability.Available,
                    ParentActorId = trustedParentActorId,
                    ParentRunId = "parent-run",
                    ParentStepId = "call-child",
                    RootRunId = "parent-run",
                    Depth = 1,
                },
            });
        var start = new StartWorkflowEvent
        {
            WorkflowName = "wf_relayed",
            RunId = runId,
            Input = "hello",
            ExecutionContextDelta = new WorkflowRunExecutionContextDelta
            {
                ClearCallerCredential = true,
                CallerCredential = new WorkflowCallerCredential
                {
                    DurableCallerCredential = new DurableCallerCredentialRef
                    {
                        Ref = "parent-durable-secret",
                        Purpose = CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
                        OwnerScopeKey = "scope-parent",
                        SubjectId = "nyxid::scope-parent",
                        SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
                    },
                    DurableCredentialCleanupResponsibility =
                        WorkflowCallerCredentialCleanupResponsibility.Owner,
                },
            },
        };

        Func<Task> act = () => harness.Agent.HandleEventAsync(
            EnvelopeFrom(trustedParentActorId, start));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must be marked as borrowed*");
        harness.Agent.State.ExecutionContext.CallerCredential.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TerminalScheduledRun_WhenVaultRevokeStalls_ShouldTimeOutAndContinue(bool completed)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero));
        var vault = new CancellationAwareStalledSecretVault();
        var runId = "run-scheduled-stalled-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId, secretVault: vault, timeProvider: clock);
        await harness.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
            CallerCredential = new WorkflowCallerCredential
            {
                DurableCallerCredential = new DurableCallerCredentialRef
                {
                    Ref = "sec-stalled",
                    Purpose = CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
                    OwnerScopeKey = "schedule:schedule-1",
                    SubjectId = "nyxid::scope-1",
                    SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
                },
            },
        }));
        harness.Publisher.Published.Clear();

        var terminalTask = completed
            ? harness.Agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
            {
                RunId = runId,
                WorkflowName = "wf_relayed",
                Success = true,
                Output = "done",
            })
            : harness.Agent.HandleWorkflowStopped(new WorkflowStoppedEvent
            {
                RunId = runId,
                WorkflowName = "wf_relayed",
                Reason = "operator stop",
            });

        await vault.RevokeStarted;
        harness.Agent.State.Status.Should().Be(completed ? "completed" : "stopped");
        terminalTask.IsCompleted.Should().BeFalse();
        harness.Publisher.Published.Should().NotContain(x => x.Audience == TopologyAudience.Parent);

        clock.Advance(TimeSpan.FromSeconds(5));

        await terminalTask;
        await vault.CancellationObserved;
        harness.Publisher.Published.Should().Contain(x => x.Audience == TopologyAudience.Parent);
    }

    [Fact]
    public async Task RelayedOwnRunCompletedFailure_FromSubActor_ShouldAdoptTerminalFailedStatus()
    {
        var harness = await CreateStartedRunAsync();

        await harness.Agent.HandleEventAsync(EnvelopeFrom(ChildActorId, new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = false,
            Error = "inner failure",
        }));

        harness.Agent.State.Status.Should().Be("failed");
        harness.Agent.State.FinalError.Should().Be("inner failure");
        harness.Agent.State.TerminalWorkflowCompletionRecorded.Should().BeTrue();
        AssertNoCrossActorSideEffects(harness);
    }

    [Fact]
    public async Task RelayedOwnRunCompletedSuccess_FromSubActor_ShouldAdoptTerminalCompletedStatus()
    {
        var harness = await CreateStartedRunAsync();

        await harness.Agent.HandleEventAsync(EnvelopeFrom(ChildActorId, new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = true,
            Output = "inner output",
        }));

        harness.Agent.State.Status.Should().Be("completed");
        harness.Agent.State.FinalOutput.Should().Be("inner output");
        harness.Agent.State.TerminalWorkflowCompletionRecorded.Should().BeTrue();
        AssertNoCrossActorSideEffects(harness);
    }

    [Fact]
    public async Task RelayedChildSubWorkflowTerminal_DifferentRunId_ShouldNotClobberParentState()
    {
        var harness = await CreateStartedRunAsync();

        await harness.Agent.HandleEventAsync(EnvelopeFrom(ChildActorId, new WorkflowCompletedEvent
        {
            RunId = harness.RunId + "-child",
            WorkflowName = "wf_child",
            Success = false,
            Error = "child failure",
        }));

        harness.Agent.State.Status.Should().Be("running");
        harness.Agent.State.FinalError.Should().BeEmpty();
        harness.Agent.State.TerminalWorkflowCompletionRecorded.Should().BeFalse();
    }

    [Fact]
    public async Task RelayedOwnRunCompleted_DuplicateTerminal_ShouldCollapseToSingleTerminal()
    {
        var harness = await CreateStartedRunAsync();

        var firstTerminal = EnvelopeFrom(ChildActorId, new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = false,
            Error = "inner failure",
        });
        await harness.Agent.HandleEventAsync(firstTerminal);
        await harness.Agent.HandleEventAsync(EnvelopeFrom(ChildActorId, new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = true,
            Output = "should be ignored",
        }));

        harness.Agent.State.Status.Should().Be("failed");
        harness.Agent.State.FinalError.Should().Be("inner failure");
        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Which.Success.Should().BeFalse();
    }

    [Fact]
    public async Task RelayedOwnRunTerminal_BeforeStart_ShouldNotBeAdopted()
    {
        // Bound but not started: RunId/ScopeId/StartedAtUtc are unset.
        var harness = await CreateRunAsync("run-not-started-" + Guid.NewGuid().ToString("N"));

        await harness.Agent.HandleEventAsync(EnvelopeFrom(ChildActorId, new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = false,
            Error = "inner failure",
        }));

        harness.Agent.State.TerminalWorkflowCompletionRecorded.Should().BeFalse();
        harness.Agent.State.Status.Should().NotBe("failed");
    }

    [Fact]
    public async Task ApiStop_FromExternalPublisher_ShouldRunFullStopNotStatusOnlyAdopt()
    {
        // C2 (06-20-observatory-run-state-feed): a legitimate manual/API stop arrives non-self
        // (publisher = api.workflow.stop) with the same RunId. It must run the FULL stop path
        // (CompleteStopAsync: cleanup + parent WorkflowLlmInvocationCompletedEvent), NOT be downgraded to a
        // status-only adopt. The typed [EventHandler] HandleWorkflowStopped fires for non-self publishers.
        var harness = await CreateStartedRunAsync();

        await harness.Agent.HandleEventAsync(EnvelopeFrom("api.workflow.stop", new WorkflowStoppedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Reason = "operator stop",
        }));

        harness.Agent.State.Status.Should().Be("stopped");
        harness.Agent.State.FinalError.Should().Be("operator stop");
        AssertRanFullStop(harness);
    }

    [Fact]
    public async Task ApiRunStop_FromExternalPublisher_ShouldRunFullStopNotStatusOnlyAdopt()
    {
        // C2: same as above for the run-stop (timeout) variant — port stop publisher
        // (workflow-run-actor-port) is non-self + same RunId and must run the full CompleteStopAsync path.
        var harness = await CreateStartedRunAsync();

        await harness.Agent.HandleEventAsync(EnvelopeFrom("workflow-run-actor-port", new WorkflowRunStoppedEvent
        {
            RunId = harness.RunId,
            Reason = "run timed out",
        }));

        harness.Agent.State.Status.Should().Be("stopped");
        harness.Agent.State.FinalError.Should().Be("run timed out");
        AssertRanFullStop(harness);
    }

    [Fact]
    public async Task DirectTypedStopHandler_ShouldRunFullStop_NotSilentNoOp()
    {
        // C2: a direct typed-handler invocation (no envelope publisher) must still run the full stop. The
        // removed IsSelfPublishedInbound() guard had made this a silent no-op.
        var harness = await CreateStartedRunAsync();

        await harness.Agent.HandleWorkflowStopped(new WorkflowStoppedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Reason = "direct stop",
        });

        harness.Agent.State.Status.Should().Be("stopped");
        harness.Agent.State.FinalError.Should().Be("direct stop");
        AssertRanFullStop(harness);
    }

    [Fact]
    public async Task CompletedRun_WithNotificationTarget_ShouldCommitOutboxBeforeDispatchingTerminal()
    {
        var harness = await CreateStartedRunAsync(includeCompletionNotificationTarget: true);

        await harness.Agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = true,
            Output = "final output",
        });

        var sent = harness.Publisher.SuccessfulSends
            .Should()
            .ContainSingle(x => x.Event is WorkflowRunTerminalNotification)
            .Subject;
        sent.TargetActorId.Should().Be("delivery-actor-1");
        var notification = sent.Event.Should().BeOfType<WorkflowRunTerminalNotification>().Subject;
        notification.DeliveryId.Should().Be("delivery-1");
        notification.WorkflowActorId.Should().Be(harness.RunId);
        notification.WorkflowRunId.Should().Be(harness.RunId);
        notification.WorkflowCommandId.Should().Be("command-1");
        notification.WorkflowCorrelationId.Should().Be("correlation-1");
        notification.Status.Should().Be(WorkflowRunTerminalStatus.Completed);
        notification.Output.Should().Be("final output");
        notification.Error.Should().BeEmpty();
        notification.TerminalAt.Should().NotBeNull();
        sent.Options?.Delivery?.OperationId.Should().Contain("delivery-1");
        sent.Options?.Delivery?.OperationId.Should().Contain("command-1");
        harness.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Dispatched);
        harness.Agent.State.PendingTerminalNotification.Should().BeNull();

        CommittedTypeOrder(harness.CommittedPublisher)
            .Should()
            .ContainInOrder(
                WorkflowCompletedEvent.Descriptor.FullName,
                WorkflowRunTerminalNotificationPreparedEvent.Descriptor.FullName,
                WorkflowRunTerminalNotificationDispatchedEvent.Descriptor.FullName);
    }

    [Fact]
    public async Task SelfOwnedToolApprovalSuspension_WithNotificationTarget_ShouldNotifyDeliveryActor()
    {
        var harness = await CreateStartedRunAsync(includeCompletionNotificationTarget: true);

        await harness.Agent.HandleEventAsync(EnvelopeFrom(harness.RunId, ToolApprovalSuspension(harness.RunId)));

        var sent = harness.Publisher.SuccessfulSends
            .Should()
            .ContainSingle(x => x.Event is WorkflowRunToolApprovalNotification)
            .Subject;
        sent.TargetActorId.Should().Be("delivery-actor-1");
        var notification = sent.Event.Should().BeOfType<WorkflowRunToolApprovalNotification>().Subject;
        notification.DeliveryId.Should().Be("delivery-1");
        notification.WorkflowActorId.Should().Be(harness.RunId);
        notification.WorkflowRunId.Should().Be(harness.RunId);
        notification.WorkflowCommandId.Should().Be("command-1");
        notification.WorkflowCorrelationId.Should().Be("correlation-1");
        notification.StepId.Should().Be("tool-step-1");
        notification.ExecutionId.Should().Be("execution-1");
        notification.ToolName.Should().Be("lark_contact_batch_resolution");
        notification.ToolCallId.Should().Be("tool-call-1");
        notification.ApprovalRequestId.Should().Be("approval-request-1");
        notification.RequestedAt.Should().NotBeNull();
        sent.Options?.Delivery?.OperationId.Should().Contain("approval-request-1");
    }

    [Fact]
    public async Task ExternalToolApprovalSuspension_ShouldNotNotifyDeliveryActor()
    {
        var harness = await CreateStartedRunAsync(includeCompletionNotificationTarget: true);

        await harness.Agent.HandleEventAsync(EnvelopeFrom("external-workflow-actor", ToolApprovalSuspension(harness.RunId)));

        harness.Publisher.SuccessfulSends
            .Select(x => x.Event)
            .OfType<WorkflowRunToolApprovalNotification>()
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task StoppedRun_WithNotificationTarget_ShouldDispatchTypedStoppedTerminal()
    {
        var harness = await CreateStartedRunAsync(includeCompletionNotificationTarget: true);

        await harness.Agent.HandleWorkflowStopped(new WorkflowStoppedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Reason = "operator stop",
        });

        var notification = harness.Publisher.SuccessfulSends
            .Select(x => x.Event)
            .OfType<WorkflowRunTerminalNotification>()
            .Should()
            .ContainSingle()
            .Subject;
        notification.Status.Should().Be(WorkflowRunTerminalStatus.Stopped);
        notification.Output.Should().BeEmpty();
        notification.Error.Should().Be("operator stop");
    }

    [Fact]
    public async Task ChatRequest_WhenWorkflowIsNotCompiled_ShouldCommitAndDispatchTerminalFailure()
    {
        var runId = "run-invalid-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId, workflowYaml: "name: [invalid");
        var request = CreateNotificationTargetRequest();

        await harness.Agent.HandleEventAsync(EnvelopeFrom(
            "api",
            request,
            envelopeId: "command-1",
            correlationId: "correlation-1"));

        CommittedEvents<WorkflowRunExecutionStartedEvent>(harness.CommittedPublisher).Should().BeEmpty();
        var terminal = CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject;
        terminal.Success.Should().BeFalse();
        terminal.Error.Should().Be("Workflow run is not definition-bound or compiled.");
        harness.Publisher.SuccessfulSends
            .Select(x => x.Event)
            .OfType<WorkflowRunTerminalNotification>()
            .Should()
            .ContainSingle()
            .Which.Error.Should().Be(terminal.Error);
        harness.Publisher.Published.Count(x => x.Event is WorkflowCompletedEvent).Should().Be(1);
        harness.Publisher.Published.Count(x => x.Event is WorkflowLlmInvocationCompletedEvent).Should().Be(1);
        CommittedTypeOrder(harness.CommittedPublisher)
            .Should()
            .ContainInOrder(
                WorkflowRunCompletionNotificationTargetAdoptedEvent.Descriptor.FullName,
                WorkflowCompletedEvent.Descriptor.FullName,
                WorkflowRunTerminalNotificationPreparedEvent.Descriptor.FullName,
                WorkflowRunTerminalNotificationDispatchedEvent.Descriptor.FullName);
    }

    [Fact]
    public async Task ChatRequest_WhenInputFileBindingFails_ShouldCommitAndDispatchTerminalFailure()
    {
        var runId = "run-file-bind-failure-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(
            runId,
            fileOwnershipPort: new ThrowingWorkflowFileArtifactOwnershipPort());
        var request = CreateNotificationTargetRequest();
        request.InputParts.Add(new WorkflowChatInputPartPayload
        {
            Kind = WorkflowChatInputPartKind.File,
            FileRef = new WorkflowFileRef
            {
                FileId = "file-1",
                ArtifactId = "workflow-file://file-1",
                SourceKind = WorkflowFileSourceKind.ChatInput,
            },
        });

        await harness.Agent.HandleEventAsync(EnvelopeFrom(
            "api",
            request,
            envelopeId: "command-1",
            correlationId: "correlation-1"));

        CommittedEvents<WorkflowRunExecutionStartedEvent>(harness.CommittedPublisher).Should().BeEmpty();
        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Which.Error.Should().Be("workflow_input_file_binding_failed");
        harness.Publisher.SuccessfulSends
            .Select(x => x.Event)
            .OfType<WorkflowRunTerminalNotification>()
            .Should()
            .ContainSingle()
            .Which.Status.Should().Be(WorkflowRunTerminalStatus.Failed);
        harness.Publisher.Published.Count(x => x.Event is WorkflowCompletedEvent).Should().Be(1);
        harness.Publisher.Published.Count(x => x.Event is WorkflowLlmInvocationCompletedEvent).Should().Be(1);
    }

    [Fact]
    public async Task ChatRequest_WhenInputFileRefAlreadyHasOwner_ShouldBindWithExistingOwner()
    {
        var runId = "run-file-prebound-" + Guid.NewGuid().ToString("N");
        var ownershipPort = new RecordingWorkflowFileArtifactOwnershipPort();
        var harness = await CreateRunAsync(runId, fileOwnershipPort: ownershipPort);
        var request = CreateNotificationTargetRequest();
        request.InputParts.Add(new WorkflowChatInputPartPayload
        {
            Kind = WorkflowChatInputPartKind.File,
            FileRef = new WorkflowFileRef
            {
                FileId = "file-1",
                ArtifactId = "workflow-file://file-1",
                SourceKind = WorkflowFileSourceKind.ChatInput,
                OwnerRunId = "source-run",
                OwnerScopeId = "source-scope",
            },
        });

        await harness.Agent.HandleEventAsync(EnvelopeFrom(
            "api",
            request,
            envelopeId: "command-1",
            correlationId: "correlation-1"));

        var bindRequest = ownershipPort.BindRequests.Should().ContainSingle().Subject;
        bindRequest.OwnerRunId.Should().Be("source-run");
        bindRequest.OwnerScopeId.Should().Be("source-scope");
        var fileRef = CommittedEvents<WorkflowRunExecutionStartedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject
            .InputFileRefs
            .Should()
            .ContainSingle()
            .Subject;
        fileRef.OwnerRunId.Should().Be("source-run");
        fileRef.OwnerScopeId.Should().Be("source-scope");
    }

    [Fact]
    public async Task ChatRequest_WhenInputFileRefHasNoOwner_ShouldBindWithCurrentRunOwner()
    {
        var runId = "run-file-ownerless-" + Guid.NewGuid().ToString("N");
        var ownershipPort = new RecordingWorkflowFileArtifactOwnershipPort();
        var harness = await CreateRunAsync(runId, fileOwnershipPort: ownershipPort);
        var request = CreateNotificationTargetRequest();
        request.InputParts.Add(new WorkflowChatInputPartPayload
        {
            Kind = WorkflowChatInputPartKind.File,
            FileRef = new WorkflowFileRef
            {
                FileId = "file-ownerless-1",
                ArtifactId = "workflow-file://file-ownerless-1",
                SourceKind = WorkflowFileSourceKind.ChatInput,
            },
        });

        await harness.Agent.HandleEventAsync(EnvelopeFrom(
            "api",
            request,
            envelopeId: "command-1",
            correlationId: "correlation-1"));

        var bindRequest = ownershipPort.BindRequests.Should().ContainSingle().Subject;
        bindRequest.OwnerRunId.Should().Be(runId);
        bindRequest.OwnerScopeId.Should().Be("scope-1");
        var fileRef = CommittedEvents<WorkflowRunExecutionStartedEvent>(harness.CommittedPublisher)
            .Should()
            .ContainSingle()
            .Subject
            .InputFileRefs
            .Should()
            .ContainSingle()
            .Subject;
        fileRef.OwnerRunId.Should().Be(runId);
        fileRef.OwnerScopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task RelayedOwnRunCompletion_WithNotificationTarget_ShouldDispatchAdoptedTerminal()
    {
        var harness = await CreateStartedRunAsync(includeCompletionNotificationTarget: true);

        await harness.Agent.HandleEventAsync(EnvelopeFrom(ChildActorId, new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = false,
            Error = "inner failure",
        }));

        var notification = harness.Publisher.SuccessfulSends
            .Select(x => x.Event)
            .OfType<WorkflowRunTerminalNotification>()
            .Should()
            .ContainSingle()
            .Subject;
        notification.Status.Should().Be(WorkflowRunTerminalStatus.Failed);
        notification.Error.Should().Be("inner failure");
        harness.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Dispatched);
    }

    [Fact]
    public async Task Reactivation_WithPendingTerminalOutbox_ShouldRecoverDispatch()
    {
        var harness = await CreateStartedRunAsync(
            includeCompletionNotificationTarget: true,
            failTerminalNotificationDispatch: true);

        await harness.Agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = false,
            Error = "boom",
        });

        harness.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.RetryScheduled);
        harness.Agent.State.PendingTerminalNotification.Should().NotBeNull();
        harness.Scheduler.TimeoutRequests.Should().ContainSingle();

        var reactivated = await CreateRunAsync(harness.RunId, harness.EventStore);

        reactivated.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Dispatched);
        reactivated.Agent.State.PendingTerminalNotification.Should().BeNull();
        reactivated.Publisher.SuccessfulSends
            .Select(x => x.Event)
            .OfType<WorkflowRunTerminalNotification>()
            .Should()
            .ContainSingle()
            .Which.Error.Should().Be("boom");
    }

    [Fact]
    public async Task Reactivation_WithTerminalTargetButMissingOutbox_ShouldPrepareAndDispatch()
    {
        var harness = await CreateStartedRunAsync(includeCompletionNotificationTarget: true);
        await AppendSeedEventAsync(harness.EventStore, harness.RunId, new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = true,
            Output = "recovered output",
        });

        var reactivated = await CreateRunAsync(harness.RunId, harness.EventStore);

        reactivated.Publisher.SuccessfulSends
            .Select(x => x.Event)
            .OfType<WorkflowRunTerminalNotification>()
            .Should()
            .ContainSingle()
            .Which.Output.Should().Be("recovered output");
        reactivated.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Dispatched);
        CommittedTypeOrder(reactivated.CommittedPublisher)
            .Should()
            .ContainInOrder(
                WorkflowRunTerminalNotificationPreparedEvent.Descriptor.FullName,
                WorkflowRunTerminalNotificationDispatchedEvent.Descriptor.FullName);
    }

    [Fact]
    public async Task Reactivation_WithPreExecutionTerminalAndAdoptedTarget_ShouldRecoverMissingOutbox()
    {
        var runId = "run-invalid-recovery-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId, workflowYaml: "name: [invalid");
        await AppendSeedEventAsync(harness.EventStore, runId, new WorkflowRunCompletionNotificationTargetAdoptedEvent
        {
            CompletionNotificationTarget = CreateCompletionNotificationTarget(),
            WorkflowRunId = runId,
            ScopeId = "scope-1",
            WorkflowCommandId = "command-1",
            WorkflowCorrelationId = "correlation-1",
            AdoptedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await AppendSeedEventAsync(harness.EventStore, runId, new WorkflowCompletedEvent
        {
            RunId = runId,
            WorkflowName = "wf_relayed",
            Success = false,
            Error = "Workflow run is not definition-bound or compiled.",
        });

        var reactivated = await CreateRunAsync(
            runId,
            harness.EventStore,
            workflowYaml: "name: [invalid");

        reactivated.Publisher.SuccessfulSends
            .Select(x => x.Event)
            .OfType<WorkflowRunTerminalNotification>()
            .Should()
            .ContainSingle()
            .Which.WorkflowCommandId.Should().Be("command-1");
        reactivated.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Dispatched);
        CommittedEvents<WorkflowRunExecutionStartedEvent>(reactivated.CommittedPublisher).Should().BeEmpty();
    }

    [Fact]
    public async Task TerminalNotificationRetry_WithStaleAttempt_ShouldNotDispatchOrAdvanceOutbox()
    {
        var harness = await CreateStartedRunAsync(
            includeCompletionNotificationTarget: true,
            failTerminalNotificationDispatch: true);
        await harness.Agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = true,
            Output = "done",
        });
        var scheduledRequest = harness.Scheduler.TimeoutRequests.Should().ContainSingle().Subject;
        var scheduledRetry = scheduledRequest.TriggerEnvelope.Payload
            .Unpack<WorkflowRunTerminalNotificationRetryFiredEvent>();
        var attemptsBeforeStale = harness.Publisher.SendAttempts.Count;
        harness.Publisher.FailTerminalNotificationDispatch = false;

        await harness.Agent.HandleEventAsync(SelfEnvelope(harness.RunId, new WorkflowRunTerminalNotificationRetryFiredEvent
        {
            DeliveryId = scheduledRetry.DeliveryId,
            WorkflowActorId = scheduledRetry.WorkflowActorId,
            WorkflowCommandId = scheduledRetry.WorkflowCommandId,
            Attempt = scheduledRetry.Attempt + 1,
        }));

        harness.Publisher.SendAttempts.Should().HaveCount(attemptsBeforeStale);
        harness.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.RetryScheduled);

        await harness.Agent.HandleEventAsync(scheduledRequest.TriggerEnvelope);

        harness.Publisher.SuccessfulSends
            .Select(x => x.Event)
            .OfType<WorkflowRunTerminalNotification>()
            .Should()
            .ContainSingle();
        harness.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Dispatched);
    }

    [Fact]
    public async Task TerminalNotificationRetry_WhenSchedulerFails_ShouldKeepPreparedAndContinueThroughSelfMessage()
    {
        var harness = await CreateStartedRunAsync(
            includeCompletionNotificationTarget: true,
            failTerminalNotificationDispatch: true);
        harness.Scheduler.Exception = new InvalidOperationException("scheduler unavailable");

        await harness.Agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = true,
            Output = "done",
        });

        harness.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Prepared);
        CommittedEvents<WorkflowRunTerminalNotificationRetryScheduledEvent>(harness.CommittedPublisher)
            .Should().BeEmpty();
        harness.Scheduler.TimeoutRequests.Should().BeEmpty();
        var selfRetry = harness.Publisher.SuccessfulSends
            .Should()
            .ContainSingle(x =>
                x.TargetActorId == harness.RunId &&
                x.Event is WorkflowRunTerminalNotificationRetryFiredEvent)
            .Subject;
        selfRetry.Options?.Delivery?.OperationId.Should().NotBeNullOrWhiteSpace();
        harness.Publisher.FailTerminalNotificationDispatch = false;

        await harness.Agent.HandleEventAsync(SelfEnvelope(
            harness.RunId,
            (WorkflowRunTerminalNotificationRetryFiredEvent)selfRetry.Event));

        CommittedEvents<WorkflowRunTerminalNotificationPreparedEvent>(harness.CommittedPublisher)
            .Select(x => x.Attempt)
            .Should()
            .Equal(0, 1);
        harness.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Dispatched);
        harness.Publisher.SuccessfulSends
            .Select(x => x.Event)
            .OfType<WorkflowRunTerminalNotification>()
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task TerminalNotificationRetry_WhenSchedulerKeepsFailing_ShouldBoundImmediateContinuationAndRecoverOnActivation()
    {
        var harness = await CreateStartedRunAsync(
            includeCompletionNotificationTarget: true,
            failTerminalNotificationDispatch: true);
        harness.Scheduler.Exception = new InvalidOperationException("scheduler unavailable");

        await harness.Agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = false,
            Error = "boom",
        });

        var selfRetry = harness.Publisher.SuccessfulSends
            .Should()
            .ContainSingle(x =>
                x.TargetActorId == harness.RunId &&
                x.Event is WorkflowRunTerminalNotificationRetryFiredEvent)
            .Subject;

        await harness.Agent.HandleEventAsync(SelfEnvelope(
            harness.RunId,
            (WorkflowRunTerminalNotificationRetryFiredEvent)selfRetry.Event));

        harness.Publisher.SuccessfulSends
            .Count(x => x.Event is WorkflowRunTerminalNotificationRetryFiredEvent)
            .Should().Be(1);
        harness.Publisher.SendAttempts
            .Count(x => x.Event is WorkflowRunTerminalNotification)
            .Should().Be(2);
        harness.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Prepared);
        harness.Agent.State.TerminalNotificationAttempt.Should().Be(1);
        harness.Agent.State.PendingTerminalNotification.Should().NotBeNull();
        CommittedEvents<WorkflowRunTerminalNotificationRetryScheduledEvent>(harness.CommittedPublisher)
            .Should().BeEmpty();

        var reactivated = await CreateRunAsync(harness.RunId, harness.EventStore);

        reactivated.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Dispatched);
        reactivated.Agent.State.PendingTerminalNotification.Should().BeNull();
        reactivated.Publisher.SuccessfulSends
            .Select(x => x.Event)
            .OfType<WorkflowRunTerminalNotification>()
            .Should()
            .ContainSingle()
            .Which.Error.Should().Be("boom");
    }

    [Fact]
    public async Task DuplicateCompletion_AfterRetryContinuationFailure_ShouldReconcilePendingOutbox()
    {
        var harness = await CreateStartedRunAsync(
            includeCompletionNotificationTarget: true,
            failTerminalNotificationDispatch: true);
        harness.Scheduler.Exception = new InvalidOperationException("scheduler unavailable");
        harness.Publisher.FailTerminalNotificationRetrySelfDispatch = true;
        var terminal = new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = false,
            Error = "boom",
        };

        var firstAttempt = () => harness.Agent.HandleWorkflowCompleted(terminal);
        await firstAttempt.Should().ThrowAsync<InvalidOperationException>();
        harness.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Prepared);
        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher).Should().ContainSingle();
        harness.Publisher.Published.Count(x => x.Event is WorkflowCompletedEvent).Should().Be(1);
        harness.Publisher.Published.Count(x => x.Event is WorkflowLlmInvocationCompletedEvent).Should().Be(1);
        harness.Publisher.FailTerminalNotificationDispatch = false;
        harness.Publisher.FailTerminalNotificationRetrySelfDispatch = false;

        await harness.Agent.HandleWorkflowCompleted(terminal.Clone());

        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher).Should().ContainSingle();
        harness.Publisher.Published.Count(x => x.Event is WorkflowCompletedEvent).Should().Be(1);
        harness.Publisher.Published.Count(x => x.Event is WorkflowLlmInvocationCompletedEvent).Should().Be(1);
        harness.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Dispatched);
        harness.Publisher.SuccessfulSends
            .Select(x => x.Event)
            .OfType<WorkflowRunTerminalNotification>()
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task DuplicateCompletion_AfterOutboxPreparePublicationFailure_ShouldReconcileWithoutRepublishingParent()
    {
        var harness = await CreateStartedRunAsync(includeCompletionNotificationTarget: true);
        harness.CommittedPublisher.FailTerminalNotificationPrepare = true;
        var terminal = new WorkflowCompletedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Success = true,
            Output = "done",
        };

        var firstAttempt = () => harness.Agent.HandleWorkflowCompleted(terminal);
        await firstAttempt.Should().ThrowAsync<CommittedStatePublicationException>();
        harness.Agent.State.Status.Should().Be("completed");
        harness.Agent.State.PendingTerminalNotification.Should().NotBeNull();
        harness.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Prepared);
        harness.Publisher.Published.Count(x => x.Event is WorkflowCompletedEvent).Should().Be(1);
        harness.Publisher.Published.Count(x => x.Event is WorkflowLlmInvocationCompletedEvent).Should().Be(1);
        harness.CommittedPublisher.FailTerminalNotificationPrepare = false;

        await harness.Agent.HandleWorkflowCompleted(terminal.Clone());

        CommittedEvents<WorkflowCompletedEvent>(harness.CommittedPublisher).Should().ContainSingle();
        harness.Publisher.Published.Count(x => x.Event is WorkflowCompletedEvent).Should().Be(1);
        harness.Publisher.Published.Count(x => x.Event is WorkflowLlmInvocationCompletedEvent).Should().Be(1);
        harness.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Dispatched);
    }

    [Fact]
    public async Task DuplicateStop_AfterRetryContinuationFailure_ShouldReconcilePendingOutbox()
    {
        var harness = await CreateStartedRunAsync(
            includeCompletionNotificationTarget: true,
            failTerminalNotificationDispatch: true);
        harness.Scheduler.Exception = new InvalidOperationException("scheduler unavailable");
        harness.Publisher.FailTerminalNotificationRetrySelfDispatch = true;
        var stopped = new WorkflowStoppedEvent
        {
            RunId = harness.RunId,
            WorkflowName = "wf_relayed",
            Reason = "operator stop",
        };

        var firstAttempt = () => harness.Agent.HandleWorkflowStopped(stopped);
        await firstAttempt.Should().ThrowAsync<InvalidOperationException>();
        harness.Agent.State.Status.Should().Be("stopped");
        harness.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Prepared);
        harness.Publisher.Published.Count(x => x.Event is WorkflowLlmInvocationCompletedEvent).Should().Be(1);
        harness.Publisher.FailTerminalNotificationDispatch = false;
        harness.Publisher.FailTerminalNotificationRetrySelfDispatch = false;

        await harness.Agent.HandleWorkflowStopped(stopped.Clone());

        CommittedEvents<WorkflowStoppedEvent>(harness.CommittedPublisher).Should().ContainSingle();
        harness.Publisher.Published.Count(x => x.Event is WorkflowLlmInvocationCompletedEvent).Should().Be(1);
        harness.Agent.State.TerminalNotificationDeliveryStatus
            .Should().Be(WorkflowRunTerminalNotificationDeliveryStatus.Dispatched);
        harness.Publisher.SuccessfulSends
            .Select(x => x.Event)
            .OfType<WorkflowRunTerminalNotification>()
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task TerminalRun_WithPendingSubWorkflowInvocation_ShouldNotRecoverOnActivation()
    {
        // C4 (06-20-observatory-run-state-feed): ApplyWorkflowCompleted does NOT clear
        // PendingSubWorkflowInvocations (those are cleared by HandleWorkflowCompleted's
        // CleanupPendingInvocationsForRunAsync, which the status-only completion-adopt path skips). So an
        // adopted-completed run can carry a stale recoverable pending invocation into activation. The
        // activation guard must skip RecoverPendingSubWorkflowInvocationsAsync for a terminal run, so a
        // terminal run never resurrects/drives in-flight child handoffs. The recording runtime is
        // unsupported: had recovery driven the handoff it would have thrown — reactivation succeeding (and
        // leaving the pending invocation untouched) proves recovery was skipped.
        var eventStore = new RecordingEventStore();
        var runId = "run-terminal-pending-" + Guid.NewGuid().ToString("N");

        // Seed the committed stream to represent an adopted-completed run that still carries a recoverable
        // pending invocation: start (-> running, sets RunId/ScopeId/StartedAtUtc), a registered pending
        // invocation (HandoffPhase=Registered, recoverable), then a terminal WorkflowCompletedEvent. Like
        // ApplyWorkflowCompleted (and the status-only adopt path), the terminal event does NOT clear the
        // pending invocation.
        var seeded = await CreateRunAsync(runId, eventStore);
        await seeded.Agent.HandleEventAsync(EnvelopeFrom("api", new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
        }));
        await SeedPendingSubWorkflowInvocationAsync(eventStore, runId);
        await SeedTerminalCompletedAsync(eventStore, runId);

        // Reactivate the terminal run: the activation guard must NOT recover the pending invocation
        // (otherwise the unsupported runtime throws). Reactivation succeeds, state is terminal, and the
        // pending invocation is left untouched (not driven).
        var reactivated = await CreateRunAsync(runId, eventStore);

        reactivated.Agent.State.Status.Should().Be("failed");
        reactivated.Agent.State.TerminalWorkflowCompletionRecorded.Should().BeTrue();
        reactivated.Agent.State.PendingSubWorkflowInvocations.Should().ContainSingle();
        reactivated.Agent.State.PendingSubWorkflowInvocations[0].HandoffPhase
            .Should().Be(SubWorkflowInvocationHandoffPhase.Registered);
    }

    private static void AssertRanFullStop(RunHarness harness)
    {
        // CompleteStopAsync publishes a parent WorkflowLlmInvocationCompletedEvent; its presence proves the
        // full stop path ran (vs a status-only transition that publishes nothing cross-actor).
        harness.Publisher.Published
            .Where(x => x.Event is WorkflowLlmInvocationCompletedEvent && x.Audience == TopologyAudience.Parent)
            .Should()
            .ContainSingle();
    }

    private static void AssertNoCrossActorSideEffects(RunHarness harness)
    {
        // The status-only adopt path must not re-run the cross-actor side effects the inner executor
        // already emitted: no parent completion publish, no LLM-invocation-completed publish.
        harness.Publisher.Published
            .Where(x => x.Audience == TopologyAudience.Parent)
            .Should()
            .BeEmpty();
        harness.Publisher.Published
            .Where(x => x.Event is WorkflowLlmInvocationCompletedEvent)
            .Should()
            .BeEmpty();
    }

    private static async Task<RunHarness> CreateStartedRunAsync(
        bool includeCompletionNotificationTarget = false,
        bool failTerminalNotificationDispatch = false)
    {
        var runId = "run-relayed-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(
            runId,
            failTerminalNotificationDispatch: failTerminalNotificationDispatch);

        var request = new WorkflowChatRequestEvent
        {
            Prompt = "hello",
            ScopeId = "scope-1",
        };
        if (includeCompletionNotificationTarget)
            request.CompletionNotificationTarget = CreateCompletionNotificationTarget();

        await harness.Agent.HandleEventAsync(EnvelopeFrom(
            "api",
            request,
            envelopeId: "command-1",
            correlationId: "correlation-1"));

        harness.Agent.State.Status.Should().Be("running");
        harness.Agent.State.RunId.Should().Be(runId);
        harness.Agent.State.ScopeId.Should().Be("scope-1");
        harness.Agent.State.StartedAtUtc.Should().NotBeNull();

        // Clear the start-path publishes so each test only observes adopt-path side effects.
        harness.Publisher.Published.Clear();
        return harness;
    }

    private static async Task<RunHarness> CreateRunAsync(
        string runId,
        RecordingEventStore? eventStore = null,
        bool failTerminalNotificationDispatch = false,
        string? workflowYaml = null,
        Aevatar.Workflow.Application.Abstractions.Runs.IFileArtifactOwnershipPort? fileOwnershipPort = null,
        ISecretVault? secretVault = null,
        TimeProvider? timeProvider = null,
        WorkflowRunLineage? initialLineage = null)
    {
        eventStore ??= new RecordingEventStore();
        var committedHook = new RecordingCommittedStatePublicationHook();
        var topologyPublisher = new RecordingEventPublisher(runId)
        {
            FailTerminalNotificationDispatch = failTerminalNotificationDispatch,
        };
        var scheduler = new RecordingRuntimeCallbackScheduler();
        var agent = new WorkflowRunGAgent(
            new UnsupportedActorRuntime(),
            new UnsupportedActorRuntime(),
            new EmptyEventModuleFactory(),
            [new EmptyWorkflowModulePack()],
            secretVault: secretVault,
            fileArtifactOwnership: fileOwnershipPort,
            timeProvider: timeProvider)
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<WorkflowRunState>(eventStore),
            EventPublisher = topologyPublisher,
            Services = new TestServiceProvider(scheduler, committedHook),
            Logger = NullLogger.Instance,
        };
        SetAgentId(agent, runId);
        topologyPublisher.Agent = agent;
        await agent.ActivateAsync();

        // Bind only on first activation; on reactivation the definition rehydrates from the event store.
        if (string.IsNullOrWhiteSpace(agent.State.WorkflowYaml))
        {
            var binding = new BindWorkflowRunDefinitionEvent
            {
                DefinitionActorId = "definition-relayed",
                WorkflowName = "wf_relayed",
                WorkflowYaml = workflowYaml ?? WorkflowYaml(),
                RunId = runId,
                ScopeId = "scope-1",
                ExpectedExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            };
            if (initialLineage != null)
                binding.InitialLineage = initialLineage.Clone();
            await agent.HandleEventAsync(EnvelopeFrom("workflow-run-actor-port", binding));
        }

        return new RunHarness(agent, runId, eventStore, committedHook, topologyPublisher, scheduler);
    }

    private sealed class CancellationAwareStalledSecretVault : ISecretVault
    {
        private readonly TaskCompletionSource _revokeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RevokeStarted => _revokeStarted.Task;

        public Task CancellationObserved => _cancellationObserved.Task;

        public Task<StoreSecretResult> PutAsync(StoreSecretRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ResolveSecretResult> ResolveAsync(ResolveSecretRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RotateSecretResult> RotateAsync(RotateSecretRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async Task<RevokeSecretResult> RevokeAsync(
            RevokeSecretRequest request,
            CancellationToken ct = default)
        {
            _revokeStarted.TrySetResult();
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                canceled);

            await canceled.Task;
            _cancellationObserved.TrySetResult();
            ct.ThrowIfCancellationRequested();
            return new RevokeSecretResult(false);
        }
    }

    private static WorkflowChatRequestEvent CreateNotificationTargetRequest() =>
        new()
        {
            Prompt = "hello",
            ScopeId = "scope-1",
            CompletionNotificationTarget = CreateCompletionNotificationTarget(),
        };

    private static WorkflowCompletionNotificationTarget CreateCompletionNotificationTarget() =>
        new()
        {
            ActorId = "delivery-actor-1",
            DeliveryId = "delivery-1",
            ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        };

    private static WorkflowSuspendedEvent ToolApprovalSuspension(string runId) =>
        new()
        {
            RunId = runId,
            StepId = "tool-step-1",
            SuspensionType = "tool_approval",
            Prompt = "Approve contact resolution?",
            ToolApproval = new WorkflowToolApprovalSuspension
            {
                ExecutionId = "execution-1",
                ToolName = "lark_contact_batch_resolution",
                ToolCallId = "tool-call-1",
                ApprovalRequestId = "approval-request-1",
            },
        };

    // Seed a recoverable pending sub-workflow invocation directly into the run's committed event stream.
    // SubWorkflowInvocationRegisteredEvent's reducer (registered on WorkflowRunGAgent) adds the pending
    // invocation on replay; HandoffPhase=Registered keeps it recoverable (not StartDispatched/StartFailed).
    private static async Task SeedPendingSubWorkflowInvocationAsync(RecordingEventStore eventStore, string runId)
    {
        var registered = new SubWorkflowInvocationRegisteredEvent
        {
            InvocationId = runId + "-child",
            ParentRunId = runId,
            ParentStepId = "step-call",
            WorkflowName = "child_flow",
            ChildActorId = "scope-workflow:wf:run:" + runId + "-child",
            ChildRunId = runId + "-child",
            DefinitionActorId = "definition-child",
            DefinitionYaml = WorkflowYaml(),
            ScopeId = "scope-1",
            HandoffPhase = (int)SubWorkflowInvocationHandoffPhase.Registered,
        };

        await AppendSeedEventAsync(eventStore, runId, registered);
    }

    // Seed a terminal WorkflowCompletedEvent (failure) directly into the committed stream. Mirrors the
    // status-only completion-adopt path: ApplyWorkflowCompleted sets Status/TerminalWorkflowCompletionRecorded
    // but does NOT clear PendingSubWorkflowInvocations.
    private static async Task SeedTerminalCompletedAsync(RecordingEventStore eventStore, string runId)
    {
        await AppendSeedEventAsync(eventStore, runId, new WorkflowCompletedEvent
        {
            RunId = runId,
            WorkflowName = "wf_relayed",
            Success = false,
            Error = "inner failure",
        });
    }

    private static async Task AppendSeedEventAsync(RecordingEventStore eventStore, string runId, IMessage payload)
    {
        var version = await eventStore.GetVersionAsync(runId) + 1;
        await eventStore.AppendAsync(
            runId,
            [
                new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Version = version,
                    EventType = payload.Descriptor.FullName,
                    EventData = Any.Pack(payload),
                    AgentId = runId,
                },
            ],
            version - 1);
    }

    private static IEnumerable<T> CommittedEvents<T>(RecordingCommittedStatePublicationHook hook)
        where T : class, IMessage<T>, new()
    {
        var descriptor = new T().Descriptor;
        foreach (var published in hook.Events)
        {
            if (published.StateEvent?.EventData?.Is(descriptor) == true)
                yield return published.StateEvent.EventData.Unpack<T>();
        }
    }

    private static IEnumerable<string> CommittedTypeOrder(RecordingCommittedStatePublicationHook hook) =>
        hook.Events
            .Select(static published => published.StateEvent?.EventData?.TypeUrl)
            .Where(static typeUrl => !string.IsNullOrWhiteSpace(typeUrl))
            .Select(static typeUrl => typeUrl![(typeUrl!.LastIndexOf('/') + 1)..]);

    private static EventEnvelope EnvelopeFrom(
        string publisherActorId,
        IMessage payload,
        string? envelopeId = null,
        string? correlationId = null) =>
        new()
        {
            Id = envelopeId ?? Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(publisherActorId, TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId ?? Guid.NewGuid().ToString("N"),
            },
        };

    private static EventEnvelope SelfEnvelope(string runId, IMessage payload) =>
        EnvelopeFrom(runId, payload);

    private static string WorkflowYaml() =>
        """
        name: wf_relayed
        roles: []
        steps:
          - id: only-step
            type: transform
        """;

    private static void SetAgentId(GAgentBase agent, string agentId)
    {
        var setIdMethod = typeof(GAgentBase).GetMethod(
            "SetId",
            BindingFlags.Instance | BindingFlags.NonPublic);
        setIdMethod.Should().NotBeNull();
        setIdMethod!.Invoke(agent, [agentId]);
    }

    private sealed record RunHarness(
        WorkflowRunGAgent Agent,
        string RunId,
        RecordingEventStore EventStore,
        RecordingCommittedStatePublicationHook CommittedPublisher,
        RecordingEventPublisher Publisher,
        RecordingRuntimeCallbackScheduler Scheduler);

    private sealed class EmptyWorkflowModulePack : IWorkflowModulePack
    {
        public string Name => "test.empty";

        public IReadOnlyList<WorkflowModuleRegistration> Modules { get; } =
        [
            WorkflowModuleRegistration.Create<TransformModule>("transform"),
        ];

        public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders { get; } = [];

        public IReadOnlyList<IWorkflowModuleConfigurator> Configurators { get; } = [];
    }

    private sealed class ThrowingWorkflowFileArtifactOwnershipPort
        : Aevatar.Workflow.Application.Abstractions.Runs.IFileArtifactOwnershipPort
    {
        public ValueTask BindOwnerAsync(
            Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef fileRef,
            string ownerRunId,
            string? ownerScopeId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InvalidOperationException("file owner binding failed"));
    }

    private sealed class RecordingWorkflowFileArtifactOwnershipPort
        : Aevatar.Workflow.Application.Abstractions.Runs.IFileArtifactOwnershipPort
    {
        public List<(Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef FileRef, string OwnerRunId, string? OwnerScopeId)> BindRequests { get; } = [];

        public ValueTask BindOwnerAsync(
            Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef fileRef,
            string ownerRunId,
            string? ownerScopeId,
            CancellationToken cancellationToken = default)
        {
            BindRequests.Add((fileRef, ownerRunId, ownerScopeId));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyEventModuleFactory : IEventModuleFactory<IWorkflowExecutionContext>
    {
        public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? module)
        {
            _ = name;
            module = null;
            return false;
        }
    }

    private sealed class RecordingEventPublisher(string runId) : IEventPublisher
    {
        public List<(IMessage Event, TopologyAudience Audience)> Published { get; } = [];
        public List<(string TargetActorId, IMessage Event, EventEnvelopePublishOptions? Options)> SendAttempts { get; } = [];
        public List<(string TargetActorId, IMessage Event, EventEnvelopePublishOptions? Options)> SuccessfulSends { get; } = [];
        public bool FailTerminalNotificationDispatch { get; set; }
        public bool FailTerminalNotificationRetrySelfDispatch { get; set; }

        public async Task PublishAsync<T>(
            T evt,
            TopologyAudience audience,
            CancellationToken ct,
            EventEnvelope? sourceEnvelope,
            EventEnvelopePublishOptions? options)
            where T : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((evt, audience));

            if (audience == TopologyAudience.Self)
                await Agent.HandleEventAsync(SelfEnvelope(runId, evt), ct);
        }

        public WorkflowRunGAgent Agent { get; set; } = null!;

        public Task SendToAsync<T>(
            string targetActorId,
            T evt,
            CancellationToken ct,
            EventEnvelope? sourceEnvelope,
            EventEnvelopePublishOptions? options)
            where T : IMessage
        {
            ct.ThrowIfCancellationRequested();
            var attempt = (targetActorId, (IMessage)evt, options?.DeepClone());
            SendAttempts.Add(attempt);
            if (FailTerminalNotificationDispatch && evt is WorkflowRunTerminalNotification)
                return Task.FromException(new IOException("terminal notification transport unavailable"));
            if (FailTerminalNotificationRetrySelfDispatch && evt is WorkflowRunTerminalNotificationRetryFiredEvent)
                return Task.FromException(new InvalidOperationException("terminal notification self dispatch unavailable"));

            SuccessfulSends.Add(attempt);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCommittedStatePublicationHook : ICommittedStatePublicationHook
    {
        public List<CommittedStateEventPublished> Events { get; } = [];
        public bool FailTerminalNotificationPrepare { get; set; }

        public Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailTerminalNotificationPrepare &&
                context.Published.StateEvent?.EventData?.Is(WorkflowRunTerminalNotificationPreparedEvent.Descriptor) == true)
            {
                throw new InvalidOperationException("terminal notification prepare publication failed");
            }

            Events.Add(context.Published.Clone());
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _streams = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var stream = _streams.GetValueOrDefault(agentId) ?? [];
            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            currentVersion.Should().Be(expectedVersion);

            var committed = events.Select(x => x.Clone()).ToList();
            stream.AddRange(committed);
            _streams[agentId] = stream;

            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream.Count == 0 ? 0 : stream[^1].Version,
                CommittedEvents = { committed },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var events = _streams.GetValueOrDefault(agentId) ?? [];
            return Task.FromResult<IReadOnlyList<StateEvent>>(
                events
                    .Where(x => !fromVersion.HasValue || x.Version >= fromVersion.Value)
                    .Select(x => x.Clone())
                    .ToArray());
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var events = _streams.GetValueOrDefault(agentId) ?? [];
            return Task.FromResult(events.Count == 0 ? 0 : events[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(
            string agentId,
            long toVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_streams.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);

            var removed = stream.RemoveAll(x => x.Version <= toVersion);
            return Task.FromResult((long)removed);
        }
    }

    private sealed class TestServiceProvider(
        RecordingRuntimeCallbackScheduler scheduler,
        RecordingCommittedStatePublicationHook committedHook) : IServiceProvider
    {
        public object? GetService(System.Type serviceType)
        {
            if (serviceType == typeof(IEnumerable<IGAgentExecutionHook>))
                return Array.Empty<IGAgentExecutionHook>();
            if (serviceType == typeof(IActorRuntimeCallbackScheduler))
                return scheduler;
            if (serviceType == typeof(IEnumerable<ICommittedStatePublicationHook>))
                return new ICommittedStatePublicationHook[] { committedHook };

            return null;
        }
    }

    private sealed class RecordingRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];
        public Exception? Exception { get; set; }

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Exception != null)
                return Task.FromException<RuntimeCallbackLease>(Exception);

            TimeoutRequests.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class UnsupportedActorRuntime : IActorRuntime, IActorDispatchPort
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IActor?> GetAsync(string id) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string id) =>
            throw new NotSupportedException();

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task WorkflowStart_ShouldNotifyTargetAndReplayFromCommittedRunningState()
    {
        var runId = "run-started-notification-" + Guid.NewGuid().ToString("N");
        var harness = await CreateRunAsync(runId);

        await harness.Agent.HandleEventAsync(EnvelopeFrom(
            "api",
            CreateNotificationTargetRequest(),
            envelopeId: "command-started-1",
            correlationId: "correlation-started-1"));

        var sent = harness.Publisher.SuccessfulSends
            .Should().ContainSingle(published => published.Event is WorkflowRunStartedNotification)
            .Subject;
        sent.TargetActorId.Should().Be("delivery-actor-1");
        var started = sent.Event.Should().BeOfType<WorkflowRunStartedNotification>().Subject;
        started.DeliveryId.Should().Be("delivery-1");
        started.WorkflowActorId.Should().Be(runId);
        started.WorkflowRunId.Should().Be(runId);
        started.WorkflowCommandId.Should().Be("command-started-1");
        started.WorkflowCorrelationId.Should().Be("correlation-started-1");
        started.StartedAt.Should().NotBeNull();
        var deliveryOperationId = sent.Options?.Delivery?.OperationId;
        deliveryOperationId.Should().NotBeNullOrWhiteSpace();

        var reactivated = await CreateRunAsync(runId, harness.EventStore);
        var replayed = reactivated.Publisher.SuccessfulSends
            .Should().ContainSingle(published => published.Event is WorkflowRunStartedNotification)
            .Subject;
        replayed.TargetActorId.Should().Be("delivery-actor-1");
        replayed.Event.Should().BeEquivalentTo(started);
        replayed.Options?.Delivery?.OperationId.Should().Be(deliveryOperationId);
    }
}
