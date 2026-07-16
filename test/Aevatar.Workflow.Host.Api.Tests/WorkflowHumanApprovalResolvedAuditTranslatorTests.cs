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
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowHumanApprovalResolvedAuditTranslatorTests
{
    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_ShouldWireHumanApprovalAuditTranslatorAndMaterializer()
    {
        var services = new ServiceCollection();
        services.AddWorkflowExecutionProjectionCQRS();
        using var provider = services.BuildServiceProvider();

        provider
            .GetServices<IAuditCommittedEventTranslator>()
            .Select(static translator => translator.GetType())
            .Should()
            .Contain(typeof(WorkflowHumanApprovalResolvedAuditTranslator));
        provider
            .GetRequiredService<CommittedAuditArtifactMaterializer<WorkflowExecutionMaterializationContext>>()
            .Should()
            .NotBeNull();
    }

    [Theory]
    [InlineData(true, WorkflowHumanApprovalResolutionSource.User, "user")]
    [InlineData(false, WorkflowHumanApprovalResolutionSource.Timeout, "timeout")]
    public void Translate_ShouldRecordDecisionSurfaceOnly(
        bool approved,
        WorkflowHumanApprovalResolutionSource source,
        string expectedSourceLabel)
    {
        var translator = new WorkflowHumanApprovalResolvedAuditTranslator();
        var evt = new WorkflowHumanApprovalResolvedEvent
        {
            RunId = "run-1",
            StepId = "step-7",
            Approved = approved,
            ResolutionSource = source,
            UserInput = "SENSITIVE user input",
            EditedContent = "SENSITIVE edited content",
            Feedback = "SENSITIVE feedback",
            ResolvedContent = "SENSITIVE resolved content",
            DeliveryTargetId = "delivery-1",
        };

        var record = Translate(translator, evt);

        record.OperationName.Should().Be("workflow.human-approval.resolved");
        record.OperationKind.Should().Be(AuditOperationKind.System);
        record.ActorKind.Should().Be(AuditActorKind.System);
        record.Outcome.Should().Be(AuditOutcome.Success);
        record.SensitivityLevel.Should().Be(AuditSensitivityLevel.Restricted);
        record.Target.Kind.Should().Be("workflow_run");
        record.Target.Id.Should().Be("run-1");
        record.ScopeId.Should().Be("scope-context");
        record.Annotations.Should().Contain("approved", approved ? "true" : "false");
        record.Annotations.Should().Contain("resolution_source", expectedSourceLabel);
        record.Annotations.Should().Contain("step_id", "step-7");
        record.Annotations.Should().NotContainKey("is_destructive");

        // The approval payload must never enter the audit artifact.
        var serialized = record.ToString();
        serialized.Should().NotContain("SENSITIVE");
        new AuditRecordSanitizer().Sanitize(record).Should().NotBeNull();
    }

    [Fact]
    public void Translate_ShouldFallBackToOriginActorId_WhenRunIdMissing()
    {
        var translator = new WorkflowHumanApprovalResolvedAuditTranslator();
        var evt = new WorkflowHumanApprovalResolvedEvent
        {
            StepId = "step-7",
            Approved = true,
            ResolutionSource = WorkflowHumanApprovalResolutionSource.User,
            DeliveryTargetId = "delivery-1",
        };

        var record = Translate(translator, evt);

        record.Target.Id.Should().Be("workflow-run-actor-1");
    }

    [Fact]
    public void Translate_ShouldReturnZeroRecords_ForWrongEventType()
    {
        var translator = new WorkflowHumanApprovalResolvedAuditTranslator();

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
                Version = 17,
            },
            "workflow-run-actor-1",
            "type.googleapis.com/aevatar.workflow.WorkflowHumanApprovalResolvedEvent",
            DateTimeOffset.Parse("2026-07-10T09:00:00+00:00"),
            "command-1",
            "request-1",
            "corr-1");
}
