using System.Text;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Security;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Security;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowAuditReportSanitizerFailureTests
{
    [Fact]
    public void Sanitize_ShouldPreserveAndRedactStructuredFailureEvidence()
    {
        const string secret = "persisted-secret-value";
        var opaqueCredential = new string('A', 48);
        var fileResults = new WorkflowFileItemResultSet();
        fileResults.Results.Add(new WorkflowFileItemResult
        {
            Index = 2,
            Success = false,
            Output = $$"""{"service_password":"{{secret}}"}""",
            Error = $$"""credential={{secret}}""",
            FileRef = new WorkflowFileRef
            {
                FileId = "file-alpha",
                FileName = "alice@example.com",
                SourceKind = WorkflowFileSourceKind.Generated,
            },
        });
        var decision = new VoteAgreementDecision
        {
            Kind = AgreementDecisionKind.Agreed,
            Output = $$"""{"approval_token":"{{secret}}"}""",
            Reason = $$"""client_credential={{secret}}""",
        };
        decision.LabelCounts["approve"] = 2;

        var report = new WorkflowRunReport
        {
            FinalError = $"provider failed with Bearer {secret}",
            Steps =
            [
                new WorkflowRunStepTrace
                {
                    StepId = "normalize_person",
                    Success = false,
                    FailureOutput = $$"""{"lark_app_token":"{{secret}}","stderr":"SyntaxError"}""",
                    OutputPreview = $"preview {opaqueCredential}",
                    Error = $"token={secret}",
                    FailureOutputTruncated = true,
                    FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain,
                    RecoveryFailureKind = WorkflowRecoveryFailureKind.ConfigurationFailure,
                    RetryDisposition = WorkflowStepRetryDisposition.Forbidden,
                    FileItemResults = fileResults,
                    VoteAgreementDecision = decision,
                    LatestFailedAttempt = new WorkflowRunFailedStepAttempt
                    {
                        StepType = "tool_call",
                        TargetRole = "mailer",
                        Error = $"token={secret}",
                        FailureOutput = $$"""{"service_token":"{{secret}}","stderr":"retry failed"}""",
                        RequestParameters = new Dictionary<string, string>
                        {
                            ["service_token"] = secret,
                        },
                        FileItemResults = fileResults,
                    },
                    RequestParameters = new Dictionary<string, string>
                    {
                        ["config"] = $$"""{"service_token":"{{secret}}"}""",
                        ["opaque"] = opaqueCredential,
                    },
                },
            ],
            Operations =
            [
                new WorkflowRunOperation
                {
                    OperationId = "tool-alpha",
                    Kind = WorkflowRuntimeOperationKind.Tool,
                    Success = false,
                    ArgumentsJson = $$"""{"workspace_secret":"{{secret}}"}""",
                    ResultJson = $$"""{"service_token":"{{secret}}","stderr":"boom"}""",
                },
            ],
        };

        var sanitized = WorkflowAuditReportSanitizer.Sanitize(report);

        var step = sanitized.Steps.Should().ContainSingle().Subject;
        step.FailureOutput.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        step.FailureOutput.Should().Contain("SyntaxError");
        step.FailureOutput.Should().NotContain(secret);
        step.OutputPreview.Should().NotContain(opaqueCredential);
        step.Error.Should().NotContain(secret);
        step.RequestParameters["config"].Should().NotContain(secret);
        step.RequestParameters["opaque"].Should().Be(WorkflowAuditTextSanitizer.RedactedValue);
        step.FailureOutputTruncated.Should().BeTrue();
        step.FailureOutcome.Should().Be(WorkflowStepFailureOutcome.OutcomeUncertain);
        step.RecoveryFailureKind.Should().Be(WorkflowRecoveryFailureKind.ConfigurationFailure);
        step.RetryDisposition.Should().Be(WorkflowStepRetryDisposition.Forbidden);
        step.FileItemResults!.Results.Should().ContainSingle();
        step.FileItemResults.Results[0].Output.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        step.FileItemResults.Results[0].FileRef.FileName.Should().Be(WorkflowAuditTextSanitizer.RedactedValue);
        step.VoteAgreementDecision!.Output.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        step.LatestFailedAttempt.Should().NotBeNull();
        step.LatestFailedAttempt!.Error.Should().NotContain(secret);
        step.LatestFailedAttempt.FailureOutput.Should().Contain("retry failed");
        step.LatestFailedAttempt.FailureOutput.Should().NotContain(secret);
        step.LatestFailedAttempt.RequestParameters["service_token"]
            .Should().Be(WorkflowAuditTextSanitizer.RedactedValue);
        step.LatestFailedAttempt.FileItemResults!.Results[0].Output
            .Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);

        var operation = sanitized.Operations.Should().ContainSingle().Subject;
        operation.ArgumentsJson.Should().Contain(WorkflowAuditTextSanitizer.RedactedValue);
        operation.ResultJson.Should().Contain("stderr");
        operation.ResultJson.Should().NotContain(secret);
        sanitized.FinalError.Should().NotContain(secret);
    }

    [Fact]
    public void Sanitize_ShouldBoundNestedEvidenceAndPreserveExistingTruncationFlags()
    {
        var largeEvidence = "BEGIN-" +
                            new string('界', WorkflowAuditTextSanitizer.MaxDiagnosticEvidenceUtf8Bytes) +
                            "-END-🙂";
        var report = new WorkflowRunReport
        {
            Steps =
            [
                new WorkflowRunStepTrace
                {
                    StepId = "nested-evidence",
                    FileItemResults = new WorkflowFileItemResultSet
                    {
                        Results =
                        {
                            new WorkflowFileItemResult
                            {
                                Output = largeEvidence,
                                Error = "source-bounded error",
                                ErrorTruncated = true,
                            },
                        },
                    },
                    VoteAgreementDecision = new VoteAgreementDecision
                    {
                        Output = "source-bounded output",
                        OutputTruncated = true,
                        Reason = largeEvidence,
                    },
                },
            ],
        };

        var sanitized = WorkflowAuditReportSanitizer.Sanitize(report);

        var step = sanitized.Steps.Should().ContainSingle().Subject;
        var fileResult = step.FileItemResults!.Results.Should().ContainSingle().Subject;
        fileResult.OutputTruncated.Should().BeTrue();
        fileResult.ErrorTruncated.Should().BeTrue();
        fileResult.Error.Should().Be("source-bounded error");
        step.VoteAgreementDecision!.OutputTruncated.Should().BeTrue();
        step.VoteAgreementDecision.Output.Should().Be("source-bounded output");
        step.VoteAgreementDecision.ReasonTruncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(fileResult.Output)
            .Should().BeLessThanOrEqualTo(WorkflowFileItemResultProjectionContract.MaxEvidenceUtf8Bytes);
        Encoding.UTF8.GetByteCount(step.VoteAgreementDecision.Reason)
            .Should().BeLessThanOrEqualTo(WorkflowAuditTextSanitizer.MaxDiagnosticEvidenceUtf8Bytes);
        foreach (var text in new[] { fileResult.Output, step.VoteAgreementDecision.Reason })
        {
            text.Should().Contain(WorkflowAuditTextSanitizer.HeadTailTruncationMarker);
            text.Should().EndWith("-END-🙂");
        }
    }

    [Fact]
    public void Sanitize_ShouldBoundFileItemResultCountAndRemainStableWhenRepeated()
    {
        var fileItemResults = new WorkflowFileItemResultSet();
        fileItemResults.Results.Add(Enumerable.Range(
                0,
                WorkflowFileItemResultProjectionContract.MaxRetainedResults + 3)
            .Select(index => new WorkflowFileItemResult
            {
                Index = index,
                Output = $"output-{index}",
            }));
        var report = new WorkflowRunReport
        {
            Steps =
            [
                new WorkflowRunStepTrace
                {
                    StepId = "bounded-file-items",
                    FileItemResults = fileItemResults,
                },
            ],
        };

        var sanitizedOnce = WorkflowAuditReportSanitizer.Sanitize(report);
        var sanitizedTwice = WorkflowAuditReportSanitizer.Sanitize(sanitizedOnce);

        var firstResultSet = sanitizedOnce.Steps.Should().ContainSingle().Subject.FileItemResults!;
        var secondResultSet = sanitizedTwice.Steps.Should().ContainSingle().Subject.FileItemResults!;
        firstResultSet.SourceResultCount.Should().Be(fileItemResults.Results.Count);
        firstResultSet.ResultsTruncated.Should().BeTrue();
        firstResultSet.Results.Should().HaveCount(WorkflowFileItemResultProjectionContract.MaxRetainedResults);
        secondResultSet.SourceResultCount.Should().Be(firstResultSet.SourceResultCount);
        secondResultSet.ResultsTruncated.Should().BeTrue();
        secondResultSet.Results.Select(item => item.Index)
            .Should().Equal(firstResultSet.Results.Select(item => item.Index));
    }
}
