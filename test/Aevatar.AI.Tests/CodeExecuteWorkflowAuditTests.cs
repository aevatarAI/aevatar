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
        var legacyPort = new CompletedFailureCodeExecutionPort();
        var durablePort = new CompletedFailureDurableCodeExecutionPort();
        var tool = new NyxIdCodeExecuteTool(legacyPort, durablePort);
        var auditTrail = new RecordingAuditTrailAppender();
        var executor = new RecordingToolExecutionPort(new AdmittedAgentToolExecutor(
            new StartOnceAdmissionLedger(),
            auditTrail,
            new StableAuditIdentityHasher()));
        var source = new AgentWorkflowToolSourceAdapter(
            [new SingleToolSource(tool)],
            executor);
        var workflowTool = (await source.GetToolsAsync()).Should().ContainSingle().Subject;

        var request = new WorkflowToolExecutionRequest(
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
            },
            RuntimeContext: WorkflowToolRuntimeContext.Empty,
            InvocationAdmission: CodeExecutionAdmission("run-code-alpha/step-code-alpha"));
        var submitted = await workflowTool.ExecuteAsync(request);
        var workflowResult = await ((IWorkflowDurableOperationTool)workflowTool).ReconcileAsync(
            request,
            submitted.PendingOperation!);

        submitted.PendingOperation.Should().NotBeNull();
        legacyPort.Request.Should().BeNull();
        legacyPort.CallCount.Should().Be(0);
        durablePort.SubmitCallCount.Should().Be(1);
        durablePort.StatusCallCount.Should().Be(1);
        durablePort.ResultCallCount.Should().Be(1);
        durablePort.SubmitRequest!.Execution.Caller.ExecutionNyxIdCredential
            .Should().Be("source-readable-bearer");
        durablePort.SubmitRequest.Execution.Caller.SourceReadableNyxIdAccessToken
            .Should().Be("source-readable-bearer");

        executor.Outcome.Should().NotBeNull();
        var executionOutcome = executor.Outcome!;
        executionOutcome.TerminalInvoked.Should().BeFalse();
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

    [Fact]
    public async Task DurableWorkflowRecovery_ShouldNotResubmitKnownOperation()
    {
        var legacyPort = new CompletedFailureCodeExecutionPort();
        var durablePort = new CompletedFailureDurableCodeExecutionPort();
        var tool = new NyxIdCodeExecuteTool(legacyPort, durablePort);
        var executor = new RecordingToolExecutionPort(new AdmittedAgentToolExecutor(
            new StartOnceAdmissionLedger(),
            new RecordingAuditTrailAppender(),
            new StableAuditIdentityHasher()));
        var source = new AgentWorkflowToolSourceAdapter(
            [new SingleToolSource(tool)],
            executor);
        var workflowTool = (await source.GetToolsAsync()).Should().ContainSingle().Subject;
        var request = new WorkflowToolExecutionRequest(
            ArgumentsJson: """{"language":"python","code":"raise RuntimeError()"}""",
            RunId: "run-code-redelivery",
            StepId: "step-code-redelivery",
            ExecutionId: "execution-code-redelivery",
            CallId: "call-code-redelivery",
            ScopeId: "scope-code-redelivery",
            CallerCredential: new WorkflowCallerCredential
            {
                BearerToken = "source-readable-bearer",
                Kind = NyxIdCallerCredentialKind.SourceReadableUserBearer,
            },
            RuntimeContext: WorkflowToolRuntimeContext.Empty,
            InvocationAdmission: CodeExecutionAdmission("run-code-redelivery/step-code-redelivery"));

        var first = await workflowTool.ExecuteAsync(request);
        var recovery = await ((IWorkflowDurableOperationTool)workflowTool).ReconcileAsync(
            request,
            first.PendingOperation!);

        first.PendingOperation.Should().NotBeNull();
        recovery.Failure.Should().NotBeNull();
        recovery.Failure!.ErrorCode.Should().Be("EXECUTION_FAILED");
        legacyPort.CallCount.Should().Be(0);
        durablePort.SubmitCallCount.Should().Be(1);
        durablePort.StatusCallCount.Should().Be(1);
        durablePort.ResultCallCount.Should().Be(1);
        executor.Requests.Should().HaveCount(2);
        executor.Requests.Select(static candidate => candidate.ExecutionAttemptKind).Should().Equal(
            AgentToolExecutionAttemptKind.Initial,
            AgentToolExecutionAttemptKind.ActorRecovery);
    }

    [Fact]
    public async Task ProvisioningTimeout_ShouldCommitOnlyAllowlistedPhaseToWorkflowReceipt()
    {
        var legacyPort = new CompletedFailureCodeExecutionPort();
        var durablePort = new CompletedFailureDurableCodeExecutionPort(
            new DurableCodeExecutionFailure(
                DurableCodeExecutionFailureKind.ExecutionFailed,
                "SANDBOX_TIMEOUT",
                "Durable code execution failed before producing a result.",
                DiagnosticId: "diag-provisioning-safe",
                ProviderPhase: DurableCodeExecutionPhase.SandboxCreate));
        var tool = new NyxIdCodeExecuteTool(legacyPort, durablePort);
        var auditTrail = new RecordingAuditTrailAppender();
        var executor = new RecordingToolExecutionPort(new AdmittedAgentToolExecutor(
            new StartOnceAdmissionLedger(),
            auditTrail,
            new StableAuditIdentityHasher()));
        var source = new AgentWorkflowToolSourceAdapter(
            [new SingleToolSource(tool)],
            executor);
        var workflowTool = (await source.GetToolsAsync()).Should().ContainSingle().Subject;
        var request = new WorkflowToolExecutionRequest(
            ArgumentsJson: """{"language":"javascript","code":"console.log('must-not-escape')"}""",
            RunId: "run-provisioning-timeout",
            StepId: "step-provisioning-timeout",
            ExecutionId: "execution-provisioning-timeout",
            CallId: "call-provisioning-timeout",
            ScopeId: "scope-provisioning-timeout",
            CallerCredential: new WorkflowCallerCredential
            {
                BearerToken = "source-readable-bearer",
                Kind = NyxIdCallerCredentialKind.SourceReadableUserBearer,
            },
            RuntimeContext: WorkflowToolRuntimeContext.Empty,
            InvocationAdmission: CodeExecutionAdmission(
                "run-provisioning-timeout/step-provisioning-timeout"));

        var submitted = await workflowTool.ExecuteAsync(request);
        var workflowResult = await ((IWorkflowDurableOperationTool)workflowTool).ReconcileAsync(
            request,
            submitted.PendingOperation!);

        using var document = JsonDocument.Parse(workflowResult.ResultJson);
        document.RootElement.GetProperty("code").GetString().Should().Be("SANDBOX_TIMEOUT");
        document.RootElement.GetProperty("provider_phase").GetString().Should().Be("sandbox_create");
        executor.Outcome!.Receipt.ResultJson.Should().Be(workflowResult.ResultJson);
        workflowResult.ResultJson.Should().NotContain("must-not-escape");
        workflowResult.ResultJson.Should().NotContain("source-readable-bearer");
        workflowResult.ResultJson.Should().NotContain("op_0123456789abcdefghijklmnopqrstuv");
        workflowResult.ResultJson.Should().NotContain("svc-code-alpha");
        auditTrail.Records.Should().ContainSingle(record =>
            record.ToolExecution.ExecutionPhase == AuditToolExecutionPhase.Terminal &&
            record.ErrorCode == "SANDBOX_TIMEOUT");
    }

    private static WorkflowCapabilityInvocationAdmission CodeExecutionAdmission(string callSiteId)
    {
        var proof = new CodeExecutionCapabilityRef
        {
            UserServiceId = "svc-code-alpha",
            ServiceSlugSnapshot = CodeExecutionContract.ServiceSlug,
            CatalogServiceId = "catalog-code-alpha",
        };
        proof.ContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeCodeExecutionCapabilityDigest(
                proof.UserServiceId,
                proof.ServiceSlugSnapshot,
                proof.CatalogServiceId);
        proof.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Interactive);
        proof.AllowedExecutionModes.Add(ExternalCapabilityExecutionMode.Durable);
        return new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = callSiteId,
            Capability = new ExternalWorkflowCapabilityRef
            {
                CodeExecution = proof,
            },
        };
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

    private sealed class CompletedFailureDurableCodeExecutionPort : IDurableCodeExecutionPort
    {
        private const string ProviderOperationId = "op_0123456789abcdefghijklmnopqrstuv";
        private readonly DurableCodeExecutionFailure? _terminalFailure;

        public CompletedFailureDurableCodeExecutionPort(
            DurableCodeExecutionFailure? terminalFailure = null)
        {
            _terminalFailure = terminalFailure;
        }

        public int SubmitCallCount { get; private set; }

        public int StatusCallCount { get; private set; }

        public int ResultCallCount { get; private set; }

        public DurableCodeExecutionSubmitRequest? SubmitRequest { get; private set; }

        public Task<DurableCodeExecutionSubmitOutcome> SubmitAsync(
            DurableCodeExecutionSubmitRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            SubmitCallCount++;
            SubmitRequest = request;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new DurableCodeExecutionSubmitOutcome(
                new DurableCodeExecutionReceipt(
                    ProviderOperationId,
                    $"/executions/{ProviderOperationId}",
                    $"/executions/{ProviderOperationId}/result",
                    $"/executions/{ProviderOperationId}/cancel",
                    DurableCodeExecutionState.Queued,
                    request.Execution.Route,
                    now,
                    now.AddMinutes(10),
                    TimeSpan.FromSeconds(1)),
                null));
        }

        public Task<DurableCodeExecutionStatusOutcome> GetStatusAsync(
            DurableCodeExecutionOperationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StatusCallCount++;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new DurableCodeExecutionStatusOutcome(
                new DurableCodeExecutionSnapshot(
                    ProviderOperationId,
                    DurableCodeExecutionState.Failed,
                    DurableCodeExecutionPhase.Complete,
                    DurableCodeExecutionCleanupState.Complete,
                    2,
                    CancelRequested: false,
                    ResultAvailable: true,
                    request.Route,
                    "\"version-2\"",
                    now.AddMinutes(-1),
                    now,
                    now.AddMinutes(9),
                    now,
                    RetryAfter: null,
                    new DurableCodeExecutionProviderFailure(
                        _terminalFailure?.Code ?? "EXECUTION_FAILED",
                        _terminalFailure?.Message ?? "Code execution exited unsuccessfully.")),
                NotModified: false,
                ETag: "\"version-2\"",
                RetryAfter: null,
                Failure: null));
        }

        public Task<DurableCodeExecutionResultOutcome> GetResultAsync(
            DurableCodeExecutionOperationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ResultCallCount++;
            if (_terminalFailure is not null)
            {
                return Task.FromResult(new DurableCodeExecutionResultOutcome(
                    Outcome: null,
                    Pending: false,
                    RetryAfter: null,
                    Failure: _terminalFailure));
            }
            return Task.FromResult(new DurableCodeExecutionResultOutcome(
                CodeExecutionOutcome.CompletedWithFailure(
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
                    request.Route),
                Pending: false,
                RetryAfter: null,
                Failure: null));
        }

        public Task<DurableCodeExecutionCancelOutcome> CancelAsync(
            DurableCodeExecutionOperationRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Cancellation is not expected in this test.");
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

        public List<AgentToolExecutionRequest> Requests { get; } = [];

        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
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

    private sealed class StartOnceAdmissionLedger : IAgentToolAdmissionLedger
    {
        private bool _started;

        public Task<AgentToolAdmissionResult> TryStartAsync(
            AgentToolAdmissionFact fact,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var status = _started
                ? AgentToolAdmissionStatus.Duplicate
                : AgentToolAdmissionStatus.Started;
            _started = true;
            return Task.FromResult(new AgentToolAdmissionResult(status));
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
