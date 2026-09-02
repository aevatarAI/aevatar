using Aevatar.Audit;
using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.CommittedFacts;
using Aevatar.Audit.Core.Sanitization;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Audit;
using Aevatar.Workflow.Projection.DependencyInjection;
using Aevatar.Workflow.Projection.Orchestration;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowRunAuditCommittedEventTranslatorTests
{
    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_ShouldWireRunAndDefinitionAuditTranslators()
    {
        var services = new ServiceCollection();
        services.AddWorkflowExecutionProjectionCQRS();
        using var provider = services.BuildServiceProvider();

        var translatorTypes = provider
            .GetServices<IAuditCommittedEventTranslator>()
            .Select(static translator => translator.GetType())
            .ToArray();

        translatorTypes.Should().Contain(new[]
        {
            typeof(WorkflowRunExecutionStartedAuditTranslator),
            typeof(WorkflowCompletedAuditTranslator),
            typeof(WorkflowStoppedAuditTranslator),
            typeof(WorkflowRunStoppedAuditTranslator),
            typeof(WorkflowRunForkRequestedAuditTranslator),
            typeof(BindWorkflowRunDefinitionAuditTranslator),
            typeof(BindWorkflowDefinitionAuditTranslator),
        });

        // Definition binds flow through the Binding scope only, so its audit
        // materializer must be wired in addition to the ExecutionMaterialization one.
        provider
            .GetRequiredService<CommittedAuditArtifactMaterializer<WorkflowExecutionMaterializationContext>>()
            .Should().NotBeNull();
        provider
            .GetRequiredService<CommittedAuditArtifactMaterializer<WorkflowBindingProjectionContext>>()
            .Should().NotBeNull();
    }

    [Fact]
    public void RunExecutionStarted_ShouldRecordIdentifiersOnly()
    {
        var record = Translate(
            new WorkflowRunExecutionStartedAuditTranslator(),
            new WorkflowRunExecutionStartedEvent
            {
                RunId = "run-1",
                WorkflowName = "daily-report",
                Input = "SENSITIVE input payload",
                DefinitionActorId = "def-actor-1",
                ScopeId = "scope-1",
                Attempt = 2,
            });

        record.OperationName.Should().Be("workflow.run.started");
        record.OperationKind.Should().Be(AuditOperationKind.System);
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Confidential);
        record.Target.Kind.Should().Be("workflow_run");
        record.Target.Id.Should().Be("run-1");
        record.ScopeId.Should().Be("scope-1");
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Running);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Unspecified);
        record.Annotations.Should().Contain("workflow_name", "daily-report");
        record.Annotations.Should().Contain("definition_actor_id", "def-actor-1");
        record.Annotations.Should().Contain("attempt", "2");
        record.ToString().Should().NotContain("SENSITIVE");
    }

    [Theory]
    [InlineData(true, "succeeded", "false")]
    [InlineData(false, "failed", "true")]
    public void Completed_ShouldRecordOutcomeNotBody(bool success, string expectedOutcome, string expectedErrorPresent)
    {
        var record = Translate(
            new WorkflowCompletedAuditTranslator(),
            new WorkflowCompletedEvent
            {
                RunId = "run-2",
                WorkflowName = "daily-report",
                Success = success,
                Output = "SENSITIVE output body",
                Error = success ? string.Empty : "SENSITIVE error body",
            });

        record.OperationName.Should().Be("workflow.run.completed");
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Confidential);
        record.Target.Id.Should().Be("run-2");
        record.ScopeId.Should().Be("scope-context");
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        record.TerminalOutcome.Should().Be(success
            ? AuditTerminalOutcome.Succeeded
            : AuditTerminalOutcome.Failed);
        if (success)
            record.Failure.Should().BeNull();
        else
            record.Failure.Code.Should().Be("workflow_failed");
        record.Annotations.Should().Contain("outcome", expectedOutcome);
        record.Annotations.Should().Contain("error_present", expectedErrorPresent);
        record.Annotations.Should().NotContainKey("output");
        record.Annotations.Should().NotContainKey("error");
        record.ToString().Should().NotContain("SENSITIVE");
        new AuditRecordSanitizer().Sanitize(record).Should().NotBeNull();
    }

    [Fact]
    public void Stopped_ShouldRecordReason()
    {
        var record = Translate(
            new WorkflowStoppedAuditTranslator(),
            new WorkflowStoppedEvent
            {
                RunId = "run-3",
                WorkflowName = "daily-report",
                Reason = "cancelled-by-user",
            });

        record.OperationName.Should().Be("workflow.run.stopped");
        record.Target.Id.Should().Be("run-3");
        record.ScopeId.Should().Be("scope-context");
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Cancelled);
        record.Annotations.Should().Contain("reason", "cancelled-by-user");
        record.Annotations.Should().NotContainKey("is_destructive");
    }

    [Fact]
    public void RunStopped_ShouldUseDistinctOperationName()
    {
        var record = Translate(
            new WorkflowRunStoppedAuditTranslator(),
            new WorkflowRunStoppedEvent
            {
                RunId = "run-4",
                Reason = "superseded",
            });

        record.OperationName.Should().Be("workflow.run.stopped-run");
        record.Target.Id.Should().Be("run-4");
        record.ScopeId.Should().Be("scope-context");
        record.Annotations.Should().Contain("reason", "superseded");
    }

    [Fact]
    public void ForkRequested_ShouldRecordLineage()
    {
        var record = Translate(
            new WorkflowRunForkRequestedAuditTranslator(),
            new WorkflowRunForkRequestedEvent
            {
                SourceRunId = "run-5",
                StartAtStepId = "step-9",
                Attempt = 3,
                ScopeId = "scope-5",
            });

        record.OperationName.Should().Be("workflow.run.fork-requested");
        record.Target.Id.Should().Be("run-5");
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Accepted);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Unspecified);
        record.ScopeId.Should().Be("scope-5");
        record.Annotations.Should().Contain("start_at_step_id", "step-9");
        record.Annotations.Should().Contain("attempt", "3");
    }

    [Fact]
    public void RunDefinitionBound_ShouldRecordOriginAndScheduleNotYaml()
    {
        var record = Translate(
            new BindWorkflowRunDefinitionAuditTranslator(),
            new BindWorkflowRunDefinitionEvent
            {
                RunId = "run-6",
                WorkflowName = "daily-report",
                DefinitionActorId = "def-actor-6",
                ScopeId = "scope-6",
                RunOrigin = "service-invoke",
                ScheduleId = "sched-6",
                WorkflowYaml = "SENSITIVE yaml body",
            });

        record.OperationName.Should().Be("workflow.run.definition-bound");
        record.Target.Kind.Should().Be("workflow_run");
        record.Target.Id.Should().Be("run-6");
        record.ScopeId.Should().Be("scope-6");
        record.Annotations.Should().Contain("run_origin", "service-invoke");
        record.Annotations.Should().Contain("schedule_id", "sched-6");
        record.Annotations.Should().Contain("definition_actor_id", "def-actor-6");
        record.ToString().Should().NotContain("SENSITIVE");
    }

    [Fact]
    public void DefinitionBound_ShouldTargetDefinitionNotYaml()
    {
        var record = Translate(
            new BindWorkflowDefinitionAuditTranslator(),
            new BindWorkflowDefinitionEvent
            {
                WorkflowName = "daily-report",
                ScopeId = "scope-7",
                SourceKind = "import",
                WorkflowYaml = "SENSITIVE yaml body",
            });

        record.OperationName.Should().Be("workflow.definition.bound");
        record.Target.Kind.Should().Be("workflow_definition");
        record.Target.Id.Should().Be("daily-report");
        record.ScopeId.Should().Be("scope-7");
        record.Annotations.Should().Contain("source_kind", "import");
        record.ToString().Should().NotContain("SENSITIVE");
    }

    [Fact]
    public void Translate_ShouldReturnZeroRecords_ForWrongEventType()
    {
        var translator = new WorkflowCompletedAuditTranslator();

        var records = translator.Translate(Context(), Any.Pack(new StringValue { Value = "wrong" }));

        records.Should().BeEmpty();
    }

    private static AuditRecord Translate(IAuditCommittedEventTranslator translator, IMessage evt)
    {
        var records = translator.Translate(Context(), Any.Pack(evt));
        return records.Should().ContainSingle().Subject;
    }

    private static CommittedAuditTranslationContext Context() =>
        new(
            new EventEnvelope
            {
                Id = "envelope-command-id",
                Propagation = new EnvelopePropagation
                {
                    CorrelationId = "corr-1",
                },
            },
            new CommittedStateEventPublished
            {
                StateRoot = Any.Pack(new WorkflowRunState
                {
                    ScopeId = "scope-context",
                }),
            },
            new StateEvent
            {
                AgentId = "workflow-run-actor-1",
                EventId = "state-event-1",
                Version = 11,
            },
            "workflow-run-actor-1",
            "type.googleapis.com/aevatar.workflow.WorkflowCompletedEvent",
            DateTimeOffset.Parse("2026-07-10T09:00:00+00:00"),
            "command-1",
            "request-1",
            "corr-1");
}
