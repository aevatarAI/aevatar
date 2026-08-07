using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Integration.AI;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class CodeExecuteWorkflowAuditTests
{
    [Fact]
    public async Task NonZeroExit_ShouldPreserveOneFailureAcrossWorkflowReceiptAndAudit()
    {
        var port = new CompletedFailureCodeExecutionPort();
        var tool = new NyxIdCodeExecuteTool(port);
        var auditTrail = new RecordingAuditTrailAppender();
        var executor = new RecordingToolExecutionPort(new AdmittedAgentToolExecutor(
            new StartingAdmissionLedger(),
            auditTrail,
            new StableAuditIdentityHasher()));
        var source = new AgentWorkflowToolSourceAdapter(
            [new SingleToolSource(tool)],
            executor);
        var workflowTool = (await source.GetToolsAsync()).Should().ContainSingle().Subject;

        var workflowResult = await workflowTool.ExecuteAsync(new WorkflowToolExecutionRequest(
            ArgumentsJson: """{"language":"python","code":"raise RuntimeError()"}""",
            RunId: "run-code-alpha",
            StepId: "step-code-alpha",
            ExecutionId: "execution-code-alpha",
            CallId: "call-code-alpha",
            ScopeId: "scope-code-alpha",
            CallerCredential: new WorkflowCallerCredential
            {
                BearerToken = "source-readable-bearer",
                Kind = NyxIdCallerCredentialKind.SourceReadableUserBearer,
            }));

        port.Request.Should().NotBeNull();
        port.CallCount.Should().Be(1);
        port.Request!.Caller.NyxIdAccessToken.Should().Be("source-readable-bearer");

        executor.Outcome.Should().NotBeNull();
        var executionOutcome = executor.Outcome!;
        executionOutcome.TerminalInvoked.Should().BeTrue();
        executionOutcome.ResultJson.Should().Be(workflowResult.ResultJson);
        executionOutcome.Receipt.Status.Should().Be(AgentToolReceiptStatus.Error);
        executionOutcome.Receipt.ErrorCode.Should().Be("EXECUTION_FAILED");
        executionOutcome.Receipt.SubjectId.Should().Be("svc-code-alpha");
        executionOutcome.Receipt.ResultJson.Should().Be(workflowResult.ResultJson);

        workflowResult.Failure.Should().NotBeNull();
        workflowResult.Failure!.ErrorCode.Should().Be("EXECUTION_FAILED");
        workflowResult.Failure.ErrorMessage.Should().Be("Code execution exited unsuccessfully.");
        workflowResult.Failure.Retryable.Should().BeFalse();

        using (var document = JsonDocument.Parse(workflowResult.ResultJson))
        {
            var root = document.RootElement;
            root.GetProperty("success").GetBoolean().Should().BeFalse();
            root.GetProperty("error").GetString().Should().Be("EXECUTION_FAILED");
            root.GetProperty("code").GetString().Should().Be("EXECUTION_FAILED");
            root.GetProperty("diagnostic_id").GetString().Should().Be("diag-code-alpha");
            root.GetProperty("output").GetProperty("stdout").GetString().Should().Be("partial output");
            root.GetProperty("output").GetProperty("stderr").GetString().Should().Be("traceback");
            root.GetProperty("output").GetProperty("exit_code").GetInt32().Should().Be(7);
        }

        var terminalAudit = auditTrail.Records.Should().ContainSingle(record =>
            record.ToolExecution.ExecutionPhase == AuditToolExecutionPhase.Terminal).Which;
        terminalAudit.ErrorCode.Should().Be("EXECUTION_FAILED");
        terminalAudit.Outcome.Should().Be(AuditOutcome.Error);
        terminalAudit.TerminalOutcome.Should().Be(AuditTerminalOutcome.Failed);
        terminalAudit.Failure.Should().NotBeNull();
        terminalAudit.Failure!.Code.Should().Be("EXECUTION_FAILED");
        terminalAudit.Failure.Category.Should().Be(AuditFailureCategory.Execution);
    }

    private sealed class CompletedFailureCodeExecutionPort : ICodeExecutionPort
    {
        public int CallCount { get; private set; }

        public CodeExecutionRequest? Request { get; private set; }

        public Task<CodeExecutionOutcome> ExecuteAsync(
            CodeExecutionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            Request = request;
            return Task.FromResult(CodeExecutionOutcome.CompletedWithFailure(
                new CodeExecutionResult(
                    "partial output",
                    "traceback",
                    7,
                    "diag-code-alpha",
                    31),
                new CodeExecutionFailure(
                    CodeExecutionFailureKind.ExecutionFailed,
                    "EXECUTION_FAILED",
                    "Code execution exited unsuccessfully.",
                    "diag-code-alpha"),
                new CodeExecutionRouteIdentity(
                    CodeExecutionContract.ServiceSlug,
                    "svc-code-alpha",
                    CodeExecutionRouteIdentitySource.NyxIdUserServiceCatalog)));
        }
    }

    private sealed class SingleToolSource(IAgentTool tool) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IAgentTool>>([tool]);
        }
    }

    private sealed class RecordingToolExecutionPort(IAgentToolExecutionPort inner) : IAgentToolExecutionPort
    {
        public AgentToolExecutionOutcome? Outcome { get; private set; }

        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            Outcome = await inner.ExecuteAsync(request, ct);
            return Outcome;
        }
    }

    private sealed class StartingAdmissionLedger : IAgentToolAdmissionLedger
    {
        public Task<AgentToolAdmissionResult> TryStartAsync(
            AgentToolAdmissionFact fact,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentToolAdmissionResult(AgentToolAdmissionStatus.Started));
        }
    }

    private sealed class RecordingAuditTrailAppender : IAuditTrailAppender
    {
        public List<AuditRecord> Records { get; } = [];

        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.Add(record);
            return Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
        }
    }

    private sealed class StableAuditIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) =>
            new("audit-actor-code", "audit-key-code");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) =>
            auditActorId == "audit-actor-code" && identityKeyId == "audit-key-code";
    }
}
