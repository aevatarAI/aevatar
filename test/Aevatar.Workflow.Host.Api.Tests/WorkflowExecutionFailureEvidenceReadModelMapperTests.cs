using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionFailureEvidenceReadModelMapperTests
{
    [Fact]
    public void ToRunReport_ShouldExposeStructuredStepFailureEvidence()
    {
        var mapper = new WorkflowExecutionReadModelMapper();
        var fileItemResults = new WorkflowFileItemResultSet
        {
            Results =
            {
                new WorkflowFileItemResult
                {
                    Index = 3,
                    Success = false,
                    OutputTruncated = true,
                    Error = "extract failed",
                    ErrorTruncated = true,
                    FileRef = new WorkflowFileRef
                    {
                        FileId = "file-alpha",
                        SourceKind = WorkflowFileSourceKind.ChatInput,
                    },
                },
            },
        };
        var voteDecision = new VoteAgreementDecision
        {
            Kind = AgreementDecisionKind.Inconclusive,
            BranchKey = "needs-review",
            OutputTruncated = true,
            Reason = "quorum not reached",
            ReasonTruncated = true,
        };
        voteDecision.LabelCounts["approve"] = 1;
        var source = new WorkflowRunInsightReportDocument
        {
            Steps =
            {
                new WorkflowExecutionStepTrace
                {
                    StepId = "normalize_person",
                    SuccessWrapper = false,
                    Error = "code_execute failed",
                    FailureOutput = "stderr tail: SyntaxError",
                    FailureOutputTruncated = true,
                    FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain,
                    RecoveryFailureKind = WorkflowRecoveryFailureKind.ConfigurationFailure,
                    RetryDisposition = WorkflowStepRetryDisposition.Forbidden,
                    FileItemResults = fileItemResults,
                    VoteAgreementDecision = voteDecision,
                },
            },
        };

        var step = mapper.ToRunReport(source).Steps.Should().ContainSingle().Subject;

        step.FailureOutput.Should().Be("stderr tail: SyntaxError");
        step.FailureOutputTruncated.Should().BeTrue();
        step.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        step.RecoveryFailureKind.Should().Be(WorkflowRecoveryFailureKind.ConfigurationFailure);
        step.RetryDisposition.Should().Be(WorkflowStepRetryDisposition.Forbidden);
        step.FileItemResults.Should().NotBeSameAs(fileItemResults);
        step.FileItemResults!.Results.Should().ContainSingle(item =>
            item.Index == 3 &&
            item.FileRef.FileId == "file-alpha" &&
            item.OutputTruncated &&
            item.Error == "extract failed" &&
            item.ErrorTruncated);
        step.VoteAgreementDecision.Should().NotBeSameAs(voteDecision);
        step.VoteAgreementDecision!.Kind.Should().Be(AgreementDecisionKind.Inconclusive);
        step.VoteAgreementDecision.OutputTruncated.Should().BeTrue();
        step.VoteAgreementDecision.ReasonTruncated.Should().BeTrue();
        step.VoteAgreementDecision.LabelCounts["approve"].Should().Be(1);
    }

    [Fact]
    public void ToRunReport_ShouldMapLatestFailedAttemptWithoutUsingCurrentRetryIdentity()
    {
        var requestedAt = DateTimeOffset.UnixEpoch.AddSeconds(2);
        var completedAt = DateTimeOffset.UnixEpoch.AddSeconds(5);
        var source = new WorkflowRunInsightReportDocument
        {
            Steps =
            {
                new WorkflowExecutionStepTrace
                {
                    StepId = "send_email",
                    StepType = "connector_retry",
                    TargetRole = "retry-mailer",
                    RequestParameters = new Dictionary<string, string> { ["attempt"] = "second" },
                    LatestFailedAttempt = new WorkflowExecutionFailedStepAttemptReadModel
                    {
                        StepType = "tool_call",
                        TargetRole = "original-mailer",
                        RequestedAt = requestedAt,
                        CompletedAt = completedAt,
                        Success = false,
                        Error = "SMTP connection refused",
                        FailureOutput = "retryable transport error",
                        RequestParameters = new Dictionary<string, string> { ["attempt"] = "first" },
                        CompletionAnnotations = new Dictionary<string, string> { ["provider"] = "smtp" },
                    },
                },
            },
        };

        var step = new WorkflowExecutionReadModelMapper()
            .ToRunReport(source)
            .Steps.Should().ContainSingle().Subject;

        step.StepType.Should().Be("connector_retry");
        step.TargetRole.Should().Be("retry-mailer");
        step.RequestParameters.Should().Contain("attempt", "second");
        step.LatestFailedAttempt.Should().NotBeNull();
        step.LatestFailedAttempt!.StepType.Should().Be("tool_call");
        step.LatestFailedAttempt.TargetRole.Should().Be("original-mailer");
        step.LatestFailedAttempt.RequestedAt.Should().Be(requestedAt);
        step.LatestFailedAttempt.CompletedAt.Should().Be(completedAt);
        step.LatestFailedAttempt.DurationMs.Should().Be(3000);
        step.LatestFailedAttempt.Error.Should().Be("SMTP connection refused");
        step.LatestFailedAttempt.RequestParameters.Should().Contain("attempt", "first");
        step.LatestFailedAttempt.CompletionAnnotations.Should().Contain("provider", "smtp");
    }
}
