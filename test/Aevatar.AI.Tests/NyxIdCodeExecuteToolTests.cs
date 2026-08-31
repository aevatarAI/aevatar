using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Foundation.Abstractions.Credentials;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.AI.Tests;

public sealed class NyxIdCodeExecuteToolTests : IDisposable
{
    private static readonly CodeExecutionRouteIdentity ResolvedRoute = new(
        "chrono-sandbox",
        "svc-code-alpha",
        CodeExecutionRouteIdentitySource.NyxIdUserServiceCatalog);

    [Fact]
    public async Task ToolSource_WithoutCodePort_DoesNotExposeCodeExecute()
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var source = new NyxIdExecutionAgentToolSource(
            options,
            new NyxIdApiClient(options, new HttpClient()));

        var tools = await source.DiscoverToolsAsync();

        tools.Should().NotContain(tool => tool.Name == "code_execute");
    }

    [Fact]
    public async Task ToolSource_WithDuplicateCodePorts_FailsClosed()
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var outcome = CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute);
        var source = new NyxIdExecutionAgentToolSource(
            options,
            new NyxIdApiClient(options, new HttpClient()),
            codeExecutionPorts: [
                new StubCodeExecutionPort(outcome),
                new StubCodeExecutionPort(outcome),
            ]);

        var act = () => source.DiscoverToolsAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exactly one ICodeExecutionPort*");
    }

    [Fact]
    public void Metadata_DescribesExactSourceExecutionWithoutClaimingDeterminism()
    {
        var tool = CreateTool(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));

        tool.Name.Should().Be("code_execute");
        tool.Description.Should().Contain("caller-provided exact source code");
        tool.Description.Should().Contain("one-shot remote code runtime");
        tool.Description.Should().Contain("stdout, stderr, and exit code");
        tool.Description.Should().Contain("use codex_exec to delegate a natural-language task to an agent");
        tool.Description.Should().NotContain("deterministic");
        tool.ApprovalMode.Should().Be(ToolApprovalMode.NeverRequire);
        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        var properties = schema.RootElement.GetProperty("properties");
        properties.EnumerateObject().Select(static property => property.Name)
            .Should().Equal("language", "code", "timeout_secs");
        var timeout = properties.GetProperty("timeout_secs");
        timeout.GetProperty("type").GetString().Should().Be("integer");
        timeout.GetProperty("minimum").GetInt32().Should()
            .Be(CodeExecutionContract.MinimumTimeoutSeconds);
        timeout.GetProperty("maximum").GetInt32().Should()
            .Be(CodeExecutionContract.MaximumTimeoutSeconds);
        timeout.GetProperty("default").GetInt32().Should()
            .Be(CodeExecutionContract.DefaultTimeoutSeconds);
    }

    [Fact]
    public void ReplayContract_OneShotCodeRuntime_IsNonReplayable()
    {
        IAgentTool tool = CreateTool(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        const string arguments = """{"language":"python","code":"print(1 + 1)"}""";

        tool.GetCallSafety(arguments).Should().Be(new AgentToolCallSafety(
            RequiresApproval: null,
            IsReadOnly: true,
            IsDestructive: false));
        tool.ResolveReplayPolicy(arguments).Should().Be(AgentToolReplayPolicy.NonReplayable);
    }

    [Fact]
    public void ReplayContract_WorkflowToolCall_IsReconcilable()
    {
        IAgentTool tool = CreateTool(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with
        {
            InvocationSurface = AgentToolInvocationSurface.WorkflowToolCall,
        };

        tool.ResolveReplayPolicy("{}").Should().Be(AgentToolReplayPolicy.Reconcilable);
    }

    [Fact]
    public async Task StartOperationAsync_WorkflowUsesOpaqueOperationIdAndDoesNotInvokeLegacyExecute()
    {
        var legacy = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult("legacy", string.Empty, 0),
            ResolvedRoute));
        var durable = new StubDurableCodeExecutionPort();
        durable.SubmitOutcomes.Enqueue(new DurableCodeExecutionSubmitOutcome(
            DurableReceipt("provider-alpha"),
            null));
        var tool = new NyxIdCodeExecuteTool(legacy, durable);
        var context = SetWorkflowExecutionContext();
        var operationId = OpaqueOperationId('a');

        var result = await tool.StartOperationAsync(new AgentToolOperationStartRequest(
            operationId,
            "call-alpha",
            tool.Name,
            """{"language":"python","code":"print(42)","timeout_secs":300}""",
            context));

        result.Disposition.Should().Be(AgentToolOperationStartDisposition.Pending);
        result.PendingOperation!.OperationId.Should().Be(operationId);
        result.PendingOperation.ProviderOperationId.Should().Be("provider-alpha");
        result.PendingOperation.Status.Should().Be(AgentToolPendingOperationStatus.Queued);
        result.PendingOperation.RouteIdentitySource.Should()
            .Be(CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission);
        durable.SubmitRequests.Should().ContainSingle();
        var submitRequest = durable.SubmitRequests.Single();
        submitRequest.IdempotencyKey.Should().Be(operationId);
        submitRequest.Execution.TimeoutSeconds.Should().Be(300);
        submitRequest.Execution.Caller.ExecutionCredentialKind.Should()
            .Be(CodeExecutionNyxIdCredentialKind.Bearer);
        submitRequest.Execution.Caller.DurableOperationGrant.Should().BeNull();
        legacy.Request.Should().BeNull();
    }

    [Fact]
    public async Task StartOperationAsync_InteractiveAgentKeyUsesOrdinaryLifecycleAuthority()
    {
        var legacy = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult("legacy", string.Empty, 0),
            ResolvedRoute));
        var durable = new StubDurableCodeExecutionPort();
        durable.SubmitOutcomes.Enqueue(new DurableCodeExecutionSubmitOutcome(
            DurableReceipt("provider-interactive-agent-key"),
            null));
        var tool = new NyxIdCodeExecuteTool(legacy, durable);
        SetAgentKey("interactive-agent-key");
        var context = AgentToolRequestContext.Current! with
        {
            InvocationSurface = AgentToolInvocationSurface.WorkflowToolCall,
            OperationAdmission = CodeExecutionAdmission("us-code-admitted"),
        };
        AgentToolRequestContext.Current = context;

        var result = await tool.StartOperationAsync(new AgentToolOperationStartRequest(
            OpaqueOperationId('e'),
            "call-interactive-agent-key",
            tool.Name,
            """{"language":"python","code":"print(42)"}""",
            context));

        result.Disposition.Should().Be(AgentToolOperationStartDisposition.Pending);
        durable.SubmitRequests.Should().ContainSingle();
        var submitRequest = durable.SubmitRequests.Single();
        submitRequest.Execution.Caller.ExecutionCredentialKind.Should()
            .Be(CodeExecutionNyxIdCredentialKind.InteractiveAgentKey);
        submitRequest.Execution.Caller.ExecutionNyxIdCredential.Should()
            .Be("interactive-agent-key");
        submitRequest.Execution.Caller.DurableOperationGrant.Should().BeNull();
        legacy.Request.Should().BeNull();
    }

    [Fact]
    public async Task StartOperationAsync_AgentKeyWithExactGrantFailsBeforeUnreconcilableSubmit()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var durable = new StubDurableCodeExecutionPort();
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetScheduledAgentKeyWorkflowExecutionContext(
            DurableGrant(now));
        var operationId = OpaqueOperationId('d');

        var result = await tool.StartOperationAsync(new AgentToolOperationStartRequest(
            operationId,
            "call-agent-key",
            tool.Name,
            """{"language":"python","code":"print(42)"}""",
            context));

        result.Disposition.Should().Be(AgentToolOperationStartDisposition.Completed);
        AssertFailure(
            result.CompletedOutcome!,
            "code_execution_durable_lifecycle_authority_unavailable",
            "The scheduled NyxID credential does not carry producer-issued status, result, and cancel authority.");
        durable.SubmitRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("multiple")]
    [InlineData("expired")]
    [InlineData("mismatched")]
    [InlineData("legacy")]
    public async Task StartOperationAsync_AgentKeyWithoutOneExactActiveGrant_FailsBeforeProvider(
        string variant)
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var mismatchedGrant = DurableGrant(now);
        mismatchedGrant.UserServiceId = "us-code-other";
        var grants = variant switch
        {
            "missing" => Array.Empty<NyxIdDurableOperationGrantRef>(),
            "multiple" =>
            [
                DurableGrant(now),
                DurableGrant(now, grantId: "grant-executions-duplicate"),
            ],
            "expired" =>
            [
                DurableGrant(
                    now,
                    validFrom: now.AddHours(-2),
                    expiresAt: now),
            ],
            "mismatched" => [mismatchedGrant],
            "legacy" => [DurableGrant(now)],
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null),
        };
        var durable = new StubDurableCodeExecutionPort();
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetScheduledAgentKeyWorkflowExecutionContext(grants);
        if (variant == "legacy")
            context.DurableNyxIdCredential!.ProviderCredentialId = string.Empty;

        var result = await tool.StartOperationAsync(new AgentToolOperationStartRequest(
            OpaqueOperationId('f'),
            "call-agent-key-invalid",
            tool.Name,
            """{"language":"python","code":"print(42)"}""",
            context));

        result.Disposition.Should().Be(AgentToolOperationStartDisposition.Completed);
        result.CompletedOutcome.Should().NotBeNull();
        AssertFailure(
            result.CompletedOutcome!,
            "code_execution_durable_grant_rebind_required",
            "The scheduled NyxID credential is missing one exact active code execution grant and must be rebound.");
        durable.SubmitRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_AgentKeyWithExactGrant_RejectsSynchronousExecution()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var legacy = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult("legacy", string.Empty, 0),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(legacy, timeProvider: new FakeTimeProvider(now));
        AgentToolRequestContext.Current = SetScheduledAgentKeyWorkflowExecutionContext(
            DurableGrant(now)) with
        {
            InvocationSurface = AgentToolInvocationSurface.HumanSession,
        };

        var result = await tool.ExecuteWithOutcomeAsync(
            "call-agent-key-synchronous",
            tool.Name,
            """{"language":"python","code":"print(42)"}""");

        AssertFailure(
            result,
            "durable_operation_required",
            "Scheduled Agent Key code execution must use the durable operation contract.");
        legacy.Request.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_WorkflowSurfaceNeverFallsBackToLegacyExecute()
    {
        var legacy = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult("legacy", string.Empty, 0),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(legacy);
        SetWorkflowExecutionContext();

        var terminal = await tool.ExecuteWithOutcomeAsync(
            "call-no-fallback",
            tool.Name,
            """{"language":"python","code":"print(42)"}""");

        terminal.ResultJson.Should().Contain("code_execution_durable_context_invalid");
        legacy.Request.Should().BeNull();
    }

    [Fact]
    public async Task ReconcileOperationAsync_SubmitOutcomeUnknownRetainsOriginalDeadline()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var durable = new StubDurableCodeExecutionPort();
        var uncertain = new DurableCodeExecutionSubmitOutcome(
            null,
            new DurableCodeExecutionFailure(
                DurableCodeExecutionFailureKind.SubmissionUncertain,
                "code_execution_submission_uncertain",
                "The submit outcome is uncertain.",
                Retryable: true,
                RetryAfter: TimeSpan.FromSeconds(2)));
        durable.SubmitOutcomes.Enqueue(uncertain);
        durable.SubmitOutcomes.Enqueue(uncertain);
        var tool = new NyxIdCodeExecuteTool(
            new StubCodeExecutionPort(CodeExecutionOutcome.Failed(new CodeExecutionFailure(
                CodeExecutionFailureKind.TransportUnavailable,
                "legacy_must_not_run",
                "legacy must not run"))),
            durable,
            timeProvider);
        var context = SetWorkflowExecutionContext();
        var operationId = OpaqueOperationId('b');
        const string arguments = """{"language":"python","code":"print(42)"}""";

        var started = await tool.StartOperationAsync(new AgentToolOperationStartRequest(
            operationId,
            "call-beta",
            tool.Name,
            arguments,
            context));
        var originalDeadline = started.PendingOperation!.ExpiresAtUnixMs;
        started.PendingOperation.Status.Should()
            .Be(AgentToolPendingOperationStatus.SubmissionUncertain);
        timeProvider.Advance(TimeSpan.FromMinutes(1));

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                arguments,
                context,
                started.PendingOperation));

        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Pending);
        reconciled.PendingOperation!.ProviderOperationId.Should().BeEmpty();
        reconciled.PendingOperation.Status.Should()
            .Be(AgentToolPendingOperationStatus.SubmissionUncertain);
        reconciled.PendingOperation.ExpiresAtUnixMs.Should().Be(originalDeadline);
        durable.SubmitRequests.Should().HaveCount(2)
            .And.OnlyContain(request => request.IdempotencyKey == operationId);
    }

    [Fact]
    public async Task ReconcileOperationAsync_MissingLocalReceiptRecoversSubmitWithSameKey()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var durable = new StubDurableCodeExecutionPort();
        durable.SubmitOutcomes.Enqueue(new DurableCodeExecutionSubmitOutcome(
            DurableReceipt("provider-recovered"),
            null));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();
        var operationId = OpaqueOperationId('e');

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                """{"language":"python","code":"print(42)"}""",
                context));

        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Pending);
        reconciled.PendingOperation!.ProviderOperationId.Should().Be("provider-recovered");
        reconciled.PendingOperation.ExpiresAtUnixMs.Should().Be(
            new DateTimeOffset(2026, 8, 14, 12, 10, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        durable.SubmitRequests.Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be(operationId);
        durable.CancelRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileOperationAsync_KnownProviderNotFoundDoesNotResubmit()
    {
        var durable = new StubDurableCodeExecutionPort();
        durable.StatusOutcomes.Enqueue(new DurableCodeExecutionStatusOutcome(
            null,
            NotModified: false,
            ETag: null,
            RetryAfter: null,
            Failure: new DurableCodeExecutionFailure(
                DurableCodeExecutionFailureKind.OperationNotFound,
                "code_execution_operation_not_found",
                "The operation is not visible to this route.")));
        var tool = new NyxIdCodeExecuteTool(
            new StubCodeExecutionPort(CodeExecutionOutcome.Failed(new CodeExecutionFailure(
                CodeExecutionFailureKind.TransportUnavailable,
                "legacy_must_not_run",
                "legacy must not run"))),
            durable);
        var context = SetWorkflowExecutionContext();
        var operationId = OpaqueOperationId('c');
        var pending = PendingOperation(operationId, "provider-known");

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                """{"language":"python","code":"print(42)"}""",
                context,
                pending));

        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Completed);
        reconciled.CompletedOutcome!.ResultJson.Should().Contain("code_execution_operation_not_found");
        reconciled.CompletedOutcome.Receipt!.FailureOutcome.Should()
            .Be(AgentToolFailureOutcome.OutcomeUncertain);
        durable.StatusRequests.Should().ContainSingle();
        durable.SubmitRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileOperationAsync_ProviderOutcomeUncertainPreservesTypedFailureOutcome()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var operationId = OpaqueOperationId('0');
        var durable = new StubDurableCodeExecutionPort();
        durable.StatusOutcomes.Enqueue(new DurableCodeExecutionStatusOutcome(
            DurableSnapshot(
                "provider-known",
                DurableCodeExecutionState.OutcomeUncertain,
                now.AddMinutes(10)),
            NotModified: false,
            ETag: "\"version-2\"",
            RetryAfter: null,
            Failure: null));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                """{"language":"python","code":"print(42)"}""",
                context,
                PendingOperation(operationId, "provider-known")));

        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Completed);
        reconciled.CompletedOutcome!.Receipt!.ErrorCode.Should().Be("code_execution_outcome_uncertain");
        reconciled.CompletedOutcome.Receipt.FailureOutcome.Should()
            .Be(AgentToolFailureOutcome.OutcomeUncertain);
        durable.StatusRequests.Should().ContainSingle();
        durable.ResultRequests.Should().BeEmpty();
        durable.SubmitRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileOperationAsync_SubmitRecoveryRouteMismatchFailsBeforeResubmit()
    {
        var durable = new StubDurableCodeExecutionPort();
        var tool = new NyxIdCodeExecuteTool(
            new StubCodeExecutionPort(CodeExecutionOutcome.Failed(new CodeExecutionFailure(
                CodeExecutionFailureKind.TransportUnavailable,
                "legacy_must_not_run",
                "legacy must not run"))),
            durable);
        var context = SetWorkflowExecutionContext();
        var operationId = OpaqueOperationId('d');
        var pending = PendingOperation(operationId, string.Empty) with
        {
            StatusPath = string.Empty,
            ResultPath = string.Empty,
            CancelPath = string.Empty,
            Status = AgentToolPendingOperationStatus.SubmissionUncertain,
            UserServiceId = "different-service",
        };

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                """{"language":"python","code":"print(42)"}""",
                context,
                pending));

        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Completed);
        reconciled.CompletedOutcome!.ResultJson.Should().Contain("code_execution_admission_invalid");
        durable.StatusRequests.Should().BeEmpty();
        durable.SubmitRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileOperationAsync_V6WorkflowRejectsCatalogProvenanceWithMatchingId()
    {
        var durable = new StubDurableCodeExecutionPort();
        var tool = CreateDurableTool(durable, TimeProvider.System);
        var context = SetWorkflowExecutionContext();
        var operationId = OpaqueOperationId('4');
        var pending = PendingOperation(operationId, "provider-known") with
        {
            RouteIdentitySource = CodeExecutionRouteIdentitySource.NyxIdUserServiceCatalog,
        };

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                """{"language":"python","code":"print(42)"}""",
                context,
                pending));

        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Completed);
        reconciled.CompletedOutcome!.ResultJson.Should().Contain("code_execution_admission_invalid");
        durable.StatusRequests.Should().BeEmpty();
        durable.ResultRequests.Should().BeEmpty();
        durable.SubmitRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileOperationAsync_V4WorkflowUsesCatalogRouteFromStartedDurableReceipt()
    {
        var now = new DateTimeOffset(2026, 8, 18, 2, 34, 0, TimeSpan.Zero);
        var operationId = OpaqueOperationId('e');
        var durable = new StubDurableCodeExecutionPort();
        durable.SubmitOutcomes.Enqueue(new DurableCodeExecutionSubmitOutcome(
            DurableReceipt("provider-known") with
            {
                ResolvedRoute = ResolvedRoute,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(10),
            },
            null));
        durable.StatusOutcomes.Enqueue(new DurableCodeExecutionStatusOutcome(
            DurableSnapshot(
                "provider-known",
                DurableCodeExecutionState.Succeeded,
                now.AddMinutes(10)) with
            {
                ResolvedRoute = ResolvedRoute,
            },
            NotModified: false,
            ETag: "\"version-2\"",
            RetryAfter: null,
            Failure: null));
        durable.ResultOutcomes.Enqueue(new DurableCodeExecutionResultOutcome(
            CodeExecutionOutcome.Succeeded(
                new CodeExecutionResult("42\n", string.Empty, 0),
                ResolvedRoute),
            Pending: false,
            RetryAfter: null,
            Failure: null));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetLegacyWorkflowExecutionContext();
        const string arguments = """{"language":"python","code":"print(42)"}""";

        var started = await tool.StartOperationAsync(new AgentToolOperationStartRequest(
            operationId,
            "call-v4",
            tool.Name,
            arguments,
            context));

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                arguments,
                context,
                started.PendingOperation));

        started.Disposition.Should().Be(AgentToolOperationStartDisposition.Pending);
        started.PendingOperation!.UserServiceId.Should().Be(ResolvedRoute.UserServiceId);
        started.PendingOperation.RouteIdentitySource.Should().Be(ResolvedRoute.Source);
        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Completed);
        reconciled.CompletedOutcome!.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        durable.StatusRequests.Should().ContainSingle().Which.Route.Should().Be(ResolvedRoute);
        durable.ResultRequests.Should().ContainSingle().Which.Route.Should().Be(ResolvedRoute);
        durable.SubmitRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ReconcileOperationAsync_SubmitRecoveryKnownReceiptUsesProviderExpiry()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var originalDeadline = now.AddMinutes(2).ToUnixTimeMilliseconds();
        var durable = new StubDurableCodeExecutionPort();
        durable.SubmitOutcomes.Enqueue(new DurableCodeExecutionSubmitOutcome(
            DurableReceipt("provider-recovered") with
            {
                ExpiresAt = now.AddMinutes(30),
            },
            null));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();
        var operationId = OpaqueOperationId('f');
        var pending = PendingOperation(operationId, string.Empty) with
        {
            StatusPath = string.Empty,
            ResultPath = string.Empty,
            CancelPath = string.Empty,
            Status = AgentToolPendingOperationStatus.SubmissionUncertain,
            ETag = null,
            ExpiresAtUnixMs = originalDeadline,
        };

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                """{"language":"python","code":"print(42)"}""",
                context,
                pending));

        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Pending);
        reconciled.PendingOperation!.ProviderOperationId.Should().Be("provider-recovered");
        reconciled.PendingOperation.ExpiresAtUnixMs.Should().Be(now.AddMinutes(30).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task ReconcileOperationAsync_StatusSnapshotCannotExtendPersistedDeadline()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var originalDeadline = now.AddMinutes(2).ToUnixTimeMilliseconds();
        var operationId = OpaqueOperationId('1');
        var durable = new StubDurableCodeExecutionPort();
        durable.StatusOutcomes.Enqueue(new DurableCodeExecutionStatusOutcome(
            DurableSnapshot(
                "provider-known",
                DurableCodeExecutionState.Running,
                now.AddMinutes(30)),
            NotModified: false,
            ETag: "\"version-2\"",
            RetryAfter: TimeSpan.FromSeconds(3),
            Failure: null));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();
        var pending = PendingOperation(operationId, "provider-known") with
        {
            ExpiresAtUnixMs = originalDeadline,
        };

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                """{"language":"python","code":"print(42)"}""",
                context,
                pending));

        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Pending);
        reconciled.PendingOperation!.ExpiresAtUnixMs.Should().Be(originalDeadline);
    }

    [Fact]
    public async Task ReconcileOperationAsync_SubmitRecoveryCrossingDeadlineExpiresUnknownOperation()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var operationId = OpaqueOperationId('2');
        var durable = new StubDurableCodeExecutionPort
        {
            BeforeSubmitResponse = () => timeProvider.Advance(TimeSpan.FromSeconds(2)),
        };
        durable.SubmitOutcomes.Enqueue(new DurableCodeExecutionSubmitOutcome(
            null,
            new DurableCodeExecutionFailure(
                DurableCodeExecutionFailureKind.SubmissionUncertain,
                "code_execution_submission_uncertain",
                "The submit outcome is uncertain.",
                Retryable: true)));
        var tool = CreateDurableTool(durable, timeProvider);
        var context = SetWorkflowExecutionContext();
        var pending = PendingOperation(operationId, string.Empty) with
        {
            StatusPath = string.Empty,
            ResultPath = string.Empty,
            CancelPath = string.Empty,
            Status = AgentToolPendingOperationStatus.SubmissionUncertain,
            ETag = null,
            ExpiresAtUnixMs = now.AddSeconds(1).ToUnixTimeMilliseconds(),
        };

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                """{"language":"python","code":"print(42)"}""",
                context,
                pending));

        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Completed);
        reconciled.CompletedOutcome!.ResultJson.Should()
            .Contain("code_execution_submit_recovery_expired");
        durable.SubmitRequests.Should().ContainSingle();
        durable.CancelRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileOperationAsync_RetryableStatusCrossingDeadlineCancelsOnceAndTimesOut()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var operationId = OpaqueOperationId('3');
        var durable = new StubDurableCodeExecutionPort
        {
            BeforeStatusResponse = () => timeProvider.Advance(TimeSpan.FromSeconds(2)),
        };
        durable.StatusOutcomes.Enqueue(new DurableCodeExecutionStatusOutcome(
            null,
            NotModified: false,
            ETag: null,
            RetryAfter: TimeSpan.FromSeconds(5),
            Failure: new DurableCodeExecutionFailure(
                DurableCodeExecutionFailureKind.ServiceUnavailable,
                "code_execution_service_unavailable",
                "The provider is temporarily unavailable.",
                Retryable: true)));
        durable.CancelOutcomes.Enqueue(new DurableCodeExecutionCancelOutcome(
            DurableSnapshot(
                "provider-known",
                DurableCodeExecutionState.Running,
                now.AddMinutes(30),
                cancelRequested: true),
            null));
        var tool = CreateDurableTool(durable, timeProvider);
        var context = SetWorkflowExecutionContext();
        var pending = PendingOperation(operationId, "provider-known") with
        {
            ExpiresAtUnixMs = now.AddSeconds(1).ToUnixTimeMilliseconds(),
        };

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                """{"language":"python","code":"print(42)"}""",
                context,
                pending));

        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Completed);
        reconciled.CompletedOutcome!.ResultJson.Should().Contain("OPERATION_EXPIRED");
        durable.StatusRequests.Should().ContainSingle();
        durable.CancelRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ReconcileOperationAsync_PendingResultCrossingDeadlineCancelsOnceAndTimesOut()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var operationId = OpaqueOperationId('4');
        var durable = new StubDurableCodeExecutionPort
        {
            BeforeResultResponse = () => timeProvider.Advance(TimeSpan.FromSeconds(2)),
        };
        durable.StatusOutcomes.Enqueue(new DurableCodeExecutionStatusOutcome(
            DurableSnapshot(
                "provider-known",
                DurableCodeExecutionState.Succeeded,
                now.AddMinutes(30)),
            NotModified: false,
            ETag: "\"version-2\"",
            RetryAfter: null,
            Failure: null));
        durable.ResultOutcomes.Enqueue(new DurableCodeExecutionResultOutcome(
            null,
            Pending: true,
            RetryAfter: TimeSpan.FromSeconds(1),
            Failure: null));
        durable.CancelOutcomes.Enqueue(new DurableCodeExecutionCancelOutcome(null, null));
        var tool = CreateDurableTool(durable, timeProvider);
        var context = SetWorkflowExecutionContext();
        var pending = PendingOperation(operationId, "provider-known") with
        {
            ExpiresAtUnixMs = now.AddSeconds(1).ToUnixTimeMilliseconds(),
        };

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                """{"language":"python","code":"print(42)"}""",
                context,
                pending));

        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Completed);
        reconciled.CompletedOutcome!.ResultJson.Should().Contain("OPERATION_EXPIRED");
        durable.ResultRequests.Should().ContainSingle();
        durable.CancelRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ReconcileOperationAsync_ExpiredKnownOperationCancelsOnceWithoutPolling()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var operationId = OpaqueOperationId('5');
        var durable = new StubDurableCodeExecutionPort();
        durable.CancelOutcomes.Enqueue(new DurableCodeExecutionCancelOutcome(null, null));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();
        var pending = PendingOperation(operationId, "provider-known") with
        {
            ExpiresAtUnixMs = now.ToUnixTimeMilliseconds(),
        };

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                """{"language":"python","code":"print(42)"}""",
                context,
                pending));

        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Completed);
        reconciled.CompletedOutcome!.ResultJson.Should().Contain("OPERATION_EXPIRED");
        durable.StatusRequests.Should().BeEmpty();
        durable.ResultRequests.Should().BeEmpty();
        durable.CancelRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ReconcileOperationAsync_ExpiredKnownOperationPreservesSucceededCancellationRace()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var operationId = OpaqueOperationId('a');
        var route = new CodeExecutionRouteIdentity(
            CodeExecutionContract.ServiceSlug,
            "us-code-admitted",
            CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission);
        var durable = new StubDurableCodeExecutionPort();
        durable.CancelOutcomes.Enqueue(new DurableCodeExecutionCancelOutcome(
            DurableSnapshot("provider-known", DurableCodeExecutionState.Succeeded, now.AddMinutes(10)),
            null));
        durable.ResultOutcomes.Enqueue(new DurableCodeExecutionResultOutcome(
            CodeExecutionOutcome.Succeeded(
                new CodeExecutionResult("42\n", string.Empty, 0),
                route),
            Pending: false,
            RetryAfter: null,
            Failure: null));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();
        var pending = PendingOperation(operationId, "provider-known") with
        {
            ExpiresAtUnixMs = now.ToUnixTimeMilliseconds(),
        };

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                """{"language":"python","code":"print(42)"}""",
                context,
                pending));

        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Completed);
        reconciled.CompletedOutcome!.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        reconciled.CompletedOutcome.ResultJson.Should().Contain("42\\n");
        reconciled.CompletedOutcome.ResultJson.Should().NotContain("OPERATION_EXPIRED");
        durable.StatusRequests.Should().BeEmpty();
        durable.CancelRequests.Should().ContainSingle();
        durable.ResultRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ReconcileOperationAsync_ExpiredKnownOperationPreservesFailedCancellationRace()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var operationId = OpaqueOperationId('b');
        var durable = new StubDurableCodeExecutionPort();
        durable.CancelOutcomes.Enqueue(new DurableCodeExecutionCancelOutcome(
            DurableSnapshot("provider-known", DurableCodeExecutionState.Failed, now.AddMinutes(10)),
            null));
        durable.ResultOutcomes.Enqueue(new DurableCodeExecutionResultOutcome(
            Outcome: null,
            Pending: false,
            RetryAfter: null,
            Failure: new DurableCodeExecutionFailure(
                DurableCodeExecutionFailureKind.ExecutionFailed,
                "SANDBOX_CREATION_FAILED",
                "Sandbox creation failed.",
                Retryable: false,
                DiagnosticId: "diag-expiry-race")));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();
        var pending = PendingOperation(operationId, "provider-known") with
        {
            ExpiresAtUnixMs = now.ToUnixTimeMilliseconds(),
        };

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                """{"language":"python","code":"print(42)"}""",
                context,
                pending));

        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Completed);
        AssertFailure(
            reconciled.CompletedOutcome!,
            "SANDBOX_CREATION_FAILED",
            "Sandbox creation failed.");
        reconciled.CompletedOutcome!.Receipt!.FailureOutcome.Should()
            .Be(AgentToolFailureOutcome.CalleeConfirmed);
        reconciled.CompletedOutcome.ResultJson.Should().NotContain("OPERATION_EXPIRED");
        durable.StatusRequests.Should().BeEmpty();
        durable.CancelRequests.Should().ContainSingle();
        durable.ResultRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ReconcileOperationAsync_ExpiredKnownOperationWithEvictedResultRemainsOutcomeUncertain()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var operationId = OpaqueOperationId('d');
        var durable = new StubDurableCodeExecutionPort();
        durable.CancelOutcomes.Enqueue(new DurableCodeExecutionCancelOutcome(
            DurableSnapshot("provider-known", DurableCodeExecutionState.Succeeded, now.AddMinutes(10)),
            null));
        durable.ResultOutcomes.Enqueue(new DurableCodeExecutionResultOutcome(
            Outcome: null,
            Pending: false,
            RetryAfter: null,
            Failure: new DurableCodeExecutionFailure(
                DurableCodeExecutionFailureKind.Expired,
                "OPERATION_EXPIRED",
                "The operation result is no longer retained.",
                Retryable: false)));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();
        var pending = PendingOperation(operationId, "provider-known") with
        {
            ExpiresAtUnixMs = now.ToUnixTimeMilliseconds(),
        };

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                """{"language":"python","code":"print(42)"}""",
                context,
                pending));

        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Completed);
        AssertFailure(
            reconciled.CompletedOutcome!,
            "OPERATION_EXPIRED",
            "The operation result is no longer retained.");
        reconciled.CompletedOutcome!.Receipt!.FailureOutcome.Should()
            .Be(AgentToolFailureOutcome.OutcomeUncertain);
        durable.StatusRequests.Should().BeEmpty();
        durable.CancelRequests.Should().ContainSingle();
        durable.ResultRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ReconcileOperationAsync_ExpiredKnownOperationPreservesCancelledCancellationRace()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var operationId = OpaqueOperationId('c');
        var durable = new StubDurableCodeExecutionPort();
        durable.CancelOutcomes.Enqueue(new DurableCodeExecutionCancelOutcome(
            DurableSnapshot(
                "provider-known",
                DurableCodeExecutionState.Cancelled,
                now.AddMinutes(10),
                cancelRequested: true),
            null));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();
        var pending = PendingOperation(operationId, "provider-known") with
        {
            ExpiresAtUnixMs = now.ToUnixTimeMilliseconds(),
        };

        var reconciled = await tool.ReconcileOperationAsync(
            new AgentToolOperationReconciliationRequest(
                operationId,
                """{"language":"python","code":"print(42)"}""",
                context,
                pending));

        reconciled.Disposition.Should().Be(AgentToolOperationReconciliationDisposition.Completed);
        reconciled.CompletedOutcome!.Receipt!.ErrorCode.Should().Be("code_execution_cancelled");
        reconciled.CompletedOutcome.Receipt.FailureOutcome.Should()
            .Be(AgentToolFailureOutcome.CalleeConfirmed);
        reconciled.CompletedOutcome.ResultJson.Should().NotContain("OPERATION_EXPIRED");
        durable.StatusRequests.Should().BeEmpty();
        durable.CancelRequests.Should().ContainSingle();
        durable.ResultRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelOperationAsync_CancelRequestedSnapshotRemainsPending()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var operationId = OpaqueOperationId('6');
        var durable = new StubDurableCodeExecutionPort();
        durable.CancelOutcomes.Enqueue(new DurableCodeExecutionCancelOutcome(
            DurableSnapshot(
                "provider-known",
                DurableCodeExecutionState.Running,
                now.AddMinutes(10),
                cancelRequested: true),
            null));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();
        var pending = PendingOperation(operationId, "provider-known");

        var cancelled = await tool.CancelOperationAsync(new AgentToolOperationCancellationRequest(
            operationId,
            """{"language":"python","code":"print(42)"}""",
            context,
            pending,
            AgentToolOperationCancellationReason.WorkflowStopped,
            DeadlineUnixMs: now.AddMinutes(1).ToUnixTimeMilliseconds()));

        cancelled.Disposition.Should().Be(AgentToolOperationCancellationDisposition.Pending);
        cancelled.PendingOperation!.Status.Should().Be(AgentToolPendingOperationStatus.Running);
        durable.CancelRequests.Should().ContainSingle().Which.Should().Be(
            new DurableCodeExecutionOperationRequest(
                "provider-known",
                new CodeExecutionRouteIdentity(
                    CodeExecutionContract.ServiceSlug,
                    "us-code-admitted",
                    CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission),
                new CodeExecutionCallerContext(
                    "workflow-bearer",
                    "workflow-bearer",
                    CodeExecutionNyxIdCredentialKind.Bearer)));
    }

    [Fact]
    public async Task CancelOperationAsync_V4WorkflowUsesCatalogRouteFromDurableReceipt()
    {
        var now = new DateTimeOffset(2026, 8, 18, 2, 34, 0, TimeSpan.Zero);
        var operationId = OpaqueOperationId('f');
        var durable = new StubDurableCodeExecutionPort();
        durable.CancelOutcomes.Enqueue(new DurableCodeExecutionCancelOutcome(
            DurableSnapshot(
                "provider-known",
                DurableCodeExecutionState.Running,
                now.AddMinutes(10),
                cancelRequested: true) with
            {
                ResolvedRoute = ResolvedRoute,
            },
            null));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetLegacyWorkflowExecutionContext();
        var pending = PendingOperation(operationId, "provider-known") with
        {
            UserServiceId = ResolvedRoute.UserServiceId,
            RouteIdentitySource = ResolvedRoute.Source,
            ExpiresAtUnixMs = now.AddMinutes(10).ToUnixTimeMilliseconds(),
        };

        var cancelled = await tool.CancelOperationAsync(new AgentToolOperationCancellationRequest(
            operationId,
            """{"language":"python","code":"print(42)"}""",
            context,
            pending,
            AgentToolOperationCancellationReason.WorkflowStopped,
            DeadlineUnixMs: now.AddMinutes(1).ToUnixTimeMilliseconds()));

        cancelled.Disposition.Should().Be(AgentToolOperationCancellationDisposition.Pending);
        durable.CancelRequests.Should().ContainSingle().Which.Route.Should().Be(ResolvedRoute);
    }

    [Fact]
    public async Task CancelOperationAsync_DeadlineAfterCancelRequestedReturnsOutcomeUncertain()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var operationId = OpaqueOperationId('7');
        var durable = new StubDurableCodeExecutionPort();
        durable.CancelOutcomes.Enqueue(new DurableCodeExecutionCancelOutcome(
            DurableSnapshot(
                "provider-known",
                DurableCodeExecutionState.Running,
                now.AddMinutes(10),
                cancelRequested: true),
            null));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();

        var cancelled = await tool.CancelOperationAsync(new AgentToolOperationCancellationRequest(
            operationId,
            """{"language":"python","code":"print(42)"}""",
            context,
            PendingOperation(operationId, "provider-known"),
            AgentToolOperationCancellationReason.WorkflowStopped,
            DeadlineUnixMs: now.ToUnixTimeMilliseconds()));

        cancelled.Disposition.Should().Be(AgentToolOperationCancellationDisposition.Completed);
        cancelled.CompletedOutcome!.ResultJson.Should().Contain("code_execution_cancel_outcome_uncertain");
        cancelled.CompletedOutcome.Receipt!.FailureOutcome.Should()
            .Be(AgentToolFailureOutcome.OutcomeUncertain);
        durable.CancelRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task CancelOperationAsync_ProviderCancelledReturnsConfirmedCancellation()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var operationId = OpaqueOperationId('8');
        var durable = new StubDurableCodeExecutionPort();
        durable.CancelOutcomes.Enqueue(new DurableCodeExecutionCancelOutcome(
            DurableSnapshot(
                "provider-known",
                DurableCodeExecutionState.Cancelled,
                now.AddMinutes(10),
                cancelRequested: true),
            null));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();

        var cancelled = await tool.CancelOperationAsync(new AgentToolOperationCancellationRequest(
            operationId,
            """{"language":"python","code":"print(42)"}""",
            context,
            PendingOperation(operationId, "provider-known"),
            AgentToolOperationCancellationReason.WorkflowStopped,
            DeadlineUnixMs: now.AddMinutes(1).ToUnixTimeMilliseconds()));

        cancelled.Disposition.Should().Be(AgentToolOperationCancellationDisposition.Completed);
        cancelled.CompletedOutcome!.Receipt!.ErrorCode.Should().Be("code_execution_cancelled");
        cancelled.CompletedOutcome.Receipt.FailureOutcome.Should()
            .Be(AgentToolFailureOutcome.CalleeConfirmed);
    }

    [Fact]
    public async Task CancelOperationAsync_SucceededRaceReturnsProviderResult()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var operationId = OpaqueOperationId('9');
        var route = new CodeExecutionRouteIdentity(
            CodeExecutionContract.ServiceSlug,
            "us-code-admitted",
            CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission);
        var durable = new StubDurableCodeExecutionPort();
        durable.CancelOutcomes.Enqueue(new DurableCodeExecutionCancelOutcome(
            DurableSnapshot("provider-known", DurableCodeExecutionState.Succeeded, now.AddMinutes(10)),
            null));
        durable.ResultOutcomes.Enqueue(new DurableCodeExecutionResultOutcome(
            CodeExecutionOutcome.Succeeded(
                new CodeExecutionResult("42\n", string.Empty, 0),
                route),
            Pending: false,
            RetryAfter: null,
            Failure: null));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();

        var cancelled = await tool.CancelOperationAsync(new AgentToolOperationCancellationRequest(
            operationId,
            """{"language":"python","code":"print(42)"}""",
            context,
            PendingOperation(operationId, "provider-known"),
            AgentToolOperationCancellationReason.WorkflowStopped,
            DeadlineUnixMs: now.AddMinutes(1).ToUnixTimeMilliseconds()));

        cancelled.Disposition.Should().Be(AgentToolOperationCancellationDisposition.Completed);
        cancelled.CompletedOutcome!.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        cancelled.CompletedOutcome.ResultJson.Should().Contain("42\\n");
        durable.ResultRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task CancelOperationAsync_FailedRaceWithoutOutputReturnsProviderFailure()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var operationId = OpaqueOperationId('b');
        var durable = new StubDurableCodeExecutionPort();
        durable.CancelOutcomes.Enqueue(new DurableCodeExecutionCancelOutcome(
            DurableSnapshot("provider-known", DurableCodeExecutionState.Failed, now.AddMinutes(10)),
            null));
        durable.ResultOutcomes.Enqueue(new DurableCodeExecutionResultOutcome(
            Outcome: null,
            Pending: false,
            RetryAfter: null,
            Failure: new DurableCodeExecutionFailure(
                DurableCodeExecutionFailureKind.ExecutionFailed,
                "SANDBOX_CREATION_FAILED",
                "Sandbox creation failed.",
                Retryable: false,
                DiagnosticId: "diag-sandbox-create")));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();

        var cancelled = await tool.CancelOperationAsync(new AgentToolOperationCancellationRequest(
            operationId,
            """{"language":"python","code":"print(42)"}""",
            context,
            PendingOperation(operationId, "provider-known"),
            AgentToolOperationCancellationReason.WorkflowStopped,
            DeadlineUnixMs: now.AddMinutes(1).ToUnixTimeMilliseconds()));

        cancelled.Disposition.Should().Be(AgentToolOperationCancellationDisposition.Completed);
        AssertFailure(
            cancelled.CompletedOutcome!,
            "SANDBOX_CREATION_FAILED",
            "Sandbox creation failed.");
        cancelled.CompletedOutcome!.Receipt!.FailureOutcome.Should()
            .Be(AgentToolFailureOutcome.CalleeConfirmed);
        using var document = JsonDocument.Parse(cancelled.CompletedOutcome!.ResultJson);
        document.RootElement.GetProperty("diagnostic_id").GetString()
            .Should().Be("diag-sandbox-create");
        document.RootElement.TryGetProperty("output", out _).Should().BeFalse();
        durable.ResultRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task CancelOperationAsync_FailedRaceWithOutputReturnsProviderResult()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var operationId = OpaqueOperationId('c');
        var route = new CodeExecutionRouteIdentity(
            CodeExecutionContract.ServiceSlug,
            "us-code-admitted",
            CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission);
        var failure = new CodeExecutionFailure(
            CodeExecutionFailureKind.ExecutionFailed,
            "EXECUTION_FAILED",
            "Code execution exited unsuccessfully.",
            "diag-exit-7");
        var durable = new StubDurableCodeExecutionPort();
        durable.CancelOutcomes.Enqueue(new DurableCodeExecutionCancelOutcome(
            DurableSnapshot("provider-known", DurableCodeExecutionState.Failed, now.AddMinutes(10)),
            null));
        durable.ResultOutcomes.Enqueue(new DurableCodeExecutionResultOutcome(
            CodeExecutionOutcome.CompletedWithFailure(
                new CodeExecutionResult("partial output", "traceback", 7, "diag-exit-7", 31),
                failure,
                route),
            Pending: false,
            RetryAfter: null,
            Failure: null));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();

        var cancelled = await tool.CancelOperationAsync(new AgentToolOperationCancellationRequest(
            operationId,
            """{"language":"python","code":"raise RuntimeError()"}""",
            context,
            PendingOperation(operationId, "provider-known"),
            AgentToolOperationCancellationReason.WorkflowStopped,
            DeadlineUnixMs: now.AddMinutes(1).ToUnixTimeMilliseconds()));

        cancelled.Disposition.Should().Be(AgentToolOperationCancellationDisposition.Completed);
        AssertFailure(cancelled.CompletedOutcome!, failure.Code, failure.Message);
        cancelled.CompletedOutcome!.Receipt!.FailureOutcome.Should()
            .Be(AgentToolFailureOutcome.CalleeConfirmed);
        using var document = JsonDocument.Parse(cancelled.CompletedOutcome!.ResultJson);
        var output = document.RootElement.GetProperty("output");
        output.GetProperty("stdout").GetString().Should().Be("partial output");
        output.GetProperty("stderr").GetString().Should().Be("traceback");
        output.GetProperty("exit_code").GetInt32().Should().Be(7);
        durable.ResultRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task CancelOperationAsync_TerminalSnapshotWithMalformedResultFailsClosed()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var operationId = OpaqueOperationId('d');
        var durable = new StubDurableCodeExecutionPort();
        durable.CancelOutcomes.Enqueue(new DurableCodeExecutionCancelOutcome(
            DurableSnapshot("provider-known", DurableCodeExecutionState.Failed, now.AddMinutes(10)),
            null));
        durable.ResultOutcomes.Enqueue(new DurableCodeExecutionResultOutcome(
            Outcome: null,
            Pending: false,
            RetryAfter: null,
            Failure: null));
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();

        var cancelled = await tool.CancelOperationAsync(new AgentToolOperationCancellationRequest(
            operationId,
            """{"language":"python","code":"print(42)"}""",
            context,
            PendingOperation(operationId, "provider-known"),
            AgentToolOperationCancellationReason.WorkflowStopped,
            DeadlineUnixMs: now.ToUnixTimeMilliseconds()));

        cancelled.Disposition.Should().Be(AgentToolOperationCancellationDisposition.Completed);
        AssertFailure(
            cancelled.CompletedOutcome!,
            "code_execution_outcome_invalid",
            "Code execution returned an invalid outcome.");
        cancelled.CompletedOutcome!.ResultJson.Should()
            .NotContain("code_execution_cancel_outcome_uncertain");
        durable.ResultRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task CancelOperationAsync_SubmissionUncertainClosesOnlyAfterStopDeadline()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var operationId = OpaqueOperationId('a');
        var durable = new StubDurableCodeExecutionPort();
        var tool = CreateDurableTool(durable, new FakeTimeProvider(now));
        var context = SetWorkflowExecutionContext();
        var pending = PendingOperation(operationId, string.Empty) with
        {
            StatusPath = string.Empty,
            ResultPath = string.Empty,
            CancelPath = string.Empty,
            Status = AgentToolPendingOperationStatus.SubmissionUncertain,
        };

        var beforeDeadline = await tool.CancelOperationAsync(new AgentToolOperationCancellationRequest(
            operationId,
            """{"language":"python","code":"print(42)"}""",
            context,
            pending,
            AgentToolOperationCancellationReason.WorkflowStopped,
            DeadlineUnixMs: now.AddMinutes(1).ToUnixTimeMilliseconds()));
        var afterDeadline = await tool.CancelOperationAsync(new AgentToolOperationCancellationRequest(
            operationId,
            """{"language":"python","code":"print(42)"}""",
            context,
            pending,
            AgentToolOperationCancellationReason.WorkflowStopped,
            DeadlineUnixMs: now.ToUnixTimeMilliseconds()));

        beforeDeadline.Disposition.Should().Be(AgentToolOperationCancellationDisposition.Pending);
        afterDeadline.Disposition.Should().Be(AgentToolOperationCancellationDisposition.Completed);
        afterDeadline.CompletedOutcome!.ResultJson.Should().Contain("code_execution_cancel_outcome_uncertain");
        durable.CancelRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_SeparatesExecutionAndSourceReadableCredentials()
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult("42\n", string.Empty, 0, "diag-code-1", 17),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        SetProxyDelegation("request-delegation", "source-readable-bearer");

        var terminal = await tool.ExecuteWithOutcomeAsync(
            "call-1",
            tool.Name,
            """{"language":"python","code":"print(42)"}""");

        port.Request.Should().Be(new CodeExecutionRequest(
            CodeExecutionLanguage.Python,
            "print(42)",
            CodeExecutionContract.DefaultTimeoutSeconds,
            new CodeExecutionRouteIdentity(
                "chrono-sandbox",
                null,
                CodeExecutionRouteIdentitySource.CodeExecutionContract),
            new CodeExecutionCallerContext(
                "request-delegation",
                "source-readable-bearer",
                CodeExecutionNyxIdCredentialKind.Bearer)));
        terminal.Receipt.Should().NotBeNull();
        terminal.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        terminal.Receipt.SubjectId.Should().Be("svc-code-alpha");
        using var document = JsonDocument.Parse(terminal.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("output").GetProperty("stdout").GetString().Should().Be("42\n");
        root.GetProperty("output").GetProperty("exit_code").GetInt32().Should().Be(0);
        root.GetProperty("output").GetProperty("diagnostic_id").GetString().Should().Be("diag-code-1");
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_WorkflowAdmissionPinsExactUserServiceRoute()
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult("ok", string.Empty, 0),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "source-readable-bearer",
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            OperationAdmission = CodeExecutionAdmission("us-code-admitted"),
        };

        await tool.ExecuteWithOutcomeAsync(
            "call-admitted",
            tool.Name,
            """{"language":"javascript","code":"console.log('ok')"}""");

        port.Request!.Route.Should().Be(new CodeExecutionRouteIdentity(
            "chrono-sandbox",
            "us-code-admitted",
            CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission));
        port.Request.Caller.Should().Be(new CodeExecutionCallerContext(
            "source-readable-bearer",
            "source-readable-bearer",
            CodeExecutionNyxIdCredentialKind.Bearer));
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_WorkflowAdmissionPreservesPersonalExecutionRouteSlug()
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult("ok", string.Empty, 0),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        SetSourceReadableBearer("source-readable-bearer");
        AgentToolRequestContext.Current = AgentToolRequestContext.Current! with
        {
            OperationAdmission = CodeExecutionAdmission("us-code-aevatar") with
            {
                ServiceSlug = "chrono-sandbox-aevatar",
            },
        };

        await tool.ExecuteWithOutcomeAsync(
            "call-managed-admitted",
            tool.Name,
            """{"language":"javascript","code":"console.log('ok')"}""");

        port.Request.Should().NotBeNull();
        port.Request!.Route.Should().Be(new CodeExecutionRouteIdentity(
            "chrono-sandbox-aevatar",
            "us-code-aevatar",
            CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission));
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_InteractiveAgentKeyUsesLegacyDispatch()
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult("ok", string.Empty, 0),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        SetAgentKey("interactive-agent-key");
        AgentToolRequestContext.Current = AgentToolRequestContext.Current! with
        {
            OperationAdmission = CodeExecutionAdmission("us-code-admitted"),
        };

        await tool.ExecuteWithOutcomeAsync(
            "call-interactive-agent-key-admitted",
            tool.Name,
            """{"language":"javascript","code":"console.log('ok')"}""");

        port.Request.Should().NotBeNull();
        port.Request!.Caller.ExecutionCredentialKind.Should()
            .Be(CodeExecutionNyxIdCredentialKind.InteractiveAgentKey);
        port.Request.Caller.ExecutionNyxIdCredential.Should().Be("interactive-agent-key");
        port.Request.Caller.DurableOperationGrant.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_MismatchedWorkflowAdmissionFailsBeforeDispatch()
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult("ok", string.Empty, 0),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "source-readable-bearer",
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            OperationAdmission = CodeExecutionAdmission("us-code-admitted") with
            {
                ServiceSlug = "arbitrary-shadow",
            },
        };

        var outcome = await tool.ExecuteWithOutcomeAsync(
            "call-invalid-admission",
            tool.Name,
            """{"language":"javascript","code":"console.log('ok')"}""");

        port.Request.Should().BeNull();
        AssertFailure(
            outcome,
            "code_execution_admission_invalid",
            "The workflow code execution admission proof is invalid.");
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_IgnoresConnectedServicesPresentationText()
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "source-readable-bearer",
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            ConnectedServices = new AgentToolConnectedServicesContext(
                "- **Managed** (slug: `presentation-sandbox`)"),
        };

        await tool.ExecuteAsync("""{"language":"bash","code":"printf ok"}""");

        port.Request!.Route.Should().Be(new CodeExecutionRouteIdentity(
            "chrono-sandbox",
            null,
            CodeExecutionRouteIdentitySource.CodeExecutionContract));
    }

    [Theory]
    [InlineData("go")]
    [InlineData("java")]
    [InlineData("Python")]
    public async Task ExecuteWithOutcomeAsync_UnsupportedLanguage_FailsBeforeDispatch(string language)
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        SetSourceReadableBearer("source-readable-bearer");

        var terminal = await tool.ExecuteWithOutcomeAsync(
            "call-unsupported-language",
            tool.Name,
            JsonSerializer.Serialize(new { language, code = "source" }));

        port.Request.Should().BeNull();
        AssertFailure(
            terminal,
            "code_execution_request_invalid",
            "Language must be one of: python, javascript, typescript, bash.");
    }

    [Theory]
    [InlineData("{\"language\":\"python\",\"code\":\"print(1)\",\"timeout_secs\":true}")]
    [InlineData("{\"language\":\"python\",\"code\":\"print(1)\",\"timeout_secs\":null}")]
    [InlineData("{\"language\":\"python\",\"code\":\"print(1)\",\"timeout_secs\":1.5}")]
    [InlineData("{\"language\":\"python\",\"code\":\"print(1)\",\"timeout_secs\":\"300\"}")]
    [InlineData("{\"language\":\"python\",\"code\":\"print(1)\",\"timeout_secs\":0}")]
    [InlineData("{\"language\":\"python\",\"code\":\"print(1)\",\"timeout_secs\":601}")]
    public async Task ExecuteWithOutcomeAsync_InvalidTimeoutFailsBeforeDispatch(string arguments)
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        SetSourceReadableBearer("source-readable-bearer");

        var terminal = await tool.ExecuteWithOutcomeAsync("call-invalid-timeout", tool.Name, arguments);

        port.Request.Should().BeNull();
        AssertFailure(
            terminal,
            "code_execution_request_invalid",
            "'timeout_secs' must be an integer between 1 and 600.");
    }

    [Theory]
    [InlineData(CodeExecutionContract.MinimumTimeoutSeconds)]
    [InlineData(CodeExecutionContract.MaximumTimeoutSeconds)]
    public async Task ExecuteWithOutcomeAsync_TimeoutBoundaryDispatchesExactValue(int timeoutSeconds)
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        SetSourceReadableBearer("source-readable-bearer");

        await tool.ExecuteWithOutcomeAsync(
            "call-timeout-boundary",
            tool.Name,
            JsonSerializer.Serialize(new { language = "python", code = "print(1)", timeout_secs = timeoutSeconds }));

        port.Request.Should().NotBeNull();
        port.Request!.TimeoutSeconds.Should().Be(timeoutSeconds);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"language\":\"python\"}")]
    [InlineData("{\"code\":\"print(1)\"}")]
    public async Task ExecuteWithOutcomeAsync_InvalidArguments_ReturnsTypedFailureWithoutDispatch(
        string arguments)
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        SetSourceReadableBearer("source-readable-bearer");

        var terminal = await tool.ExecuteWithOutcomeAsync("call-invalid", tool.Name, arguments);

        port.Request.Should().BeNull();
        AssertFailure(
            terminal,
            "code_execution_request_invalid",
            "Both 'language' and 'code' are required.");
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_WithoutSourceReadableCredential_FailsBeforeDispatch()
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        SetProxyDelegation("request-delegation", sourceReadableBearer: null);

        var terminal = await tool.ExecuteWithOutcomeAsync(
            "call-no-credential",
            tool.Name,
            """{"language":"python","code":"print(1)"}""");

        port.Request.Should().BeNull();
        AssertFailure(
            terminal,
            "code_execution_credential_unavailable",
            "A source-readable NyxID credential is required to resolve the code execution route.");
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_NonZeroExit_PreservesResultAndFailureReceipt()
    {
        var failure = new CodeExecutionFailure(
            CodeExecutionFailureKind.ExecutionFailed,
            "EXECUTION_FAILED",
            "Code execution exited unsuccessfully.",
            "diag-code-2");
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.CompletedWithFailure(
            new CodeExecutionResult("partial", "traceback", 7, "diag-code-2", 31),
            failure,
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        SetSourceReadableBearer("source-readable-bearer");

        var terminal = await tool.ExecuteWithOutcomeAsync(
            "call-nonzero",
            tool.Name,
            """{"language":"python","code":"raise RuntimeError()"}""");

        terminal.Receipt.Should().NotBeNull();
        terminal.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        terminal.Receipt.ErrorCode.Should().Be("EXECUTION_FAILED");
        terminal.Receipt.SubjectId.Should().Be("svc-code-alpha");
        terminal.Receipt.ResultJson.Should().Be(terminal.ResultJson);
        using var document = JsonDocument.Parse(terminal.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("error").GetString().Should().Be("EXECUTION_FAILED");
        root.GetProperty("code").GetString().Should().Be("EXECUTION_FAILED");
        root.GetProperty("message").GetString().Should().Be(failure.Message);
        root.GetProperty("diagnostic_id").GetString().Should().Be("diag-code-2");
        root.GetProperty("output").GetProperty("stdout").GetString().Should().Be("partial");
        root.GetProperty("output").GetProperty("stderr").GetString().Should().Be("traceback");
        root.GetProperty("output").GetProperty("exit_code").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_TransportFailure_PreservesTypedPublicEnvelope()
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Failed(
            new CodeExecutionFailure(
                CodeExecutionFailureKind.TimedOut,
                "code_execution_timed_out",
                "Code execution timed out.",
                "diag-code-timeout")));
        var tool = new NyxIdCodeExecuteTool(port);
        SetSourceReadableBearer("source-readable-bearer");

        var terminal = await tool.ExecuteWithOutcomeAsync(
            "call-timeout",
            tool.Name,
            """{"language":"javascript","code":"while (true) {}"}""");

        AssertFailure(terminal, "code_execution_timed_out", "Code execution timed out.");
        using var document = JsonDocument.Parse(terminal.ResultJson);
        document.RootElement.GetProperty("diagnostic_id").GetString()
            .Should().Be("diag-code-timeout");
        document.RootElement.TryGetProperty("output", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_ProvisioningTimeoutCommitsSafeProviderPhase()
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Failed(
            new CodeExecutionFailure(
                CodeExecutionFailureKind.TimedOut,
                "SANDBOX_TIMEOUT",
                "Code execution timed out upstream.",
                "diag-provider-phase",
                DurableCodeExecutionPhase.SandboxCreate)));
        var tool = new NyxIdCodeExecuteTool(port);
        SetSourceReadableBearer("source-readable-bearer");

        var terminal = await tool.ExecuteWithOutcomeAsync(
            "call-provider-phase",
            tool.Name,
            """{"language":"javascript","code":"console.log('must-not-escape')"}""");

        using var document = JsonDocument.Parse(terminal.ResultJson);
        var root = document.RootElement;
        root.GetProperty("code").GetString().Should().Be("SANDBOX_TIMEOUT");
        root.GetProperty("provider_phase").GetString().Should().Be("sandbox_create");
        terminal.ResultJson.Should().NotContain("must-not-escape");
        terminal.ResultJson.Should().NotContain("source-readable-bearer");
        terminal.ResultJson.Should().NotContain("us-code-alpha");
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_ContradictoryPortOutcome_FailsClosed()
    {
        var port = new StubCodeExecutionPort(new CodeExecutionOutcome(
            new CodeExecutionResult("unexpected", string.Empty, 0),
            new CodeExecutionFailure(
                CodeExecutionFailureKind.ExecutionFailed,
                "EXECUTION_FAILED",
                "Code execution exited unsuccessfully."),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        SetSourceReadableBearer("source-readable-bearer");

        var terminal = await tool.ExecuteWithOutcomeAsync(
            "call-invalid-outcome",
            tool.Name,
            """{"language":"python","code":"print(1)"}""");

        AssertFailure(
            terminal,
            "code_execution_outcome_invalid",
            "Code execution returned an invalid outcome.");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"success\":true,\"output\":{\"stdout\":\"ok\",\"stderr\":\"\",\"exit_code\":1}}")]
    [InlineData("{\"success\":false,\"error\":\"EXECUTION_FAILED\",\"code\":\"OTHER\",\"message\":\"failed\"}")]
    [InlineData("{\"success\":false,\"error\":\"EXECUTION_FAILED\",\"code\":\"EXECUTION_FAILED\",\"message\":\"failed\",\"output\":{\"stdout\":\"\",\"stderr\":\"\",\"exit_code\":0}}")]
    public void CreateResultReceipt_ContradictoryEnvelope_RemainsUnverified(string resultJson)
    {
        IAgentTool tool = CreateTool(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));

        tool.CreateResultReceipt("call-unverified", tool.Name, "{}", resultJson).Should().BeNull();
    }

    [Theory]
    [InlineData("EXECUTION_FAILED")]
    [InlineData("DEPENDENCY_INSTALL_FAILED")]
    public void CreateResultReceipt_WhenCompletedFailureOmitsOutput_RemainsUnverified(string code)
    {
        IAgentTool tool = CreateTool(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        var resultJson = JsonSerializer.Serialize(new
        {
            success = false,
            error = code,
            code,
            message = "failed",
        });

        tool.CreateResultReceipt("call-missing-output", tool.Name, "{}", resultJson)
            .Should().BeNull();
    }

    [Theory]
    [InlineData("UNAUTHENTICATED")]
    [InlineData("FORBIDDEN")]
    public void CreateResultReceipt_WhenChronoAuthorizationFailureIsTyped_PreservesReceipt(string code)
    {
        IAgentTool tool = CreateTool(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        var resultJson = JsonSerializer.Serialize(new
        {
            success = false,
            error = code,
            code,
            message = "Code execution authorization failed upstream.",
        });

        var receipt = tool.CreateResultReceipt("call-auth-failure", tool.Name, "{}", resultJson);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be(code);
        receipt.ResultJson.Should().Be(resultJson);
    }

    [Fact]
    public void CreateResultReceipt_WhenOperationResultExpired_RemainsOutcomeUncertain()
    {
        IAgentTool tool = CreateTool(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        const string resultJson =
            """{"success":false,"error":"OPERATION_EXPIRED","code":"OPERATION_EXPIRED","message":"The operation result is no longer retained."}""";

        var receipt = tool.CreateResultReceipt(
            "call-operation-expired",
            tool.Name,
            "{}",
            resultJson);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be("OPERATION_EXPIRED");
        receipt.FailureOutcome.Should().Be(AgentToolFailureOutcome.OutcomeUncertain);
        receipt.ResultJson.Should().Be(resultJson);
    }

    [Fact]
    public void CreateResultReceipt_WhenFailureCodeIsNotOwned_RemainsUnverified()
    {
        IAgentTool tool = CreateTool(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));

        tool.CreateResultReceipt(
                "call-unknown-code",
                tool.Name,
                "{}",
                """{"success":false,"error":"UNKNOWN_PROVIDER_CODE","code":"UNKNOWN_PROVIDER_CODE","message":"failed"}""")
            .Should().BeNull();
    }

    public void Dispose()
    {
        AgentToolRequestContext.Current = null;
        GC.SuppressFinalize(this);
    }

    private static NyxIdCodeExecuteTool CreateTool(CodeExecutionOutcome outcome) =>
        new(new StubCodeExecutionPort(outcome));

    private static NyxIdCodeExecuteTool CreateDurableTool(
        IDurableCodeExecutionPort durable,
        TimeProvider timeProvider) =>
        new(
            new StubCodeExecutionPort(CodeExecutionOutcome.Failed(new CodeExecutionFailure(
                CodeExecutionFailureKind.TransportUnavailable,
                "legacy_must_not_run",
                "legacy must not run"))),
            durable,
            timeProvider);

    private static AgentToolOperationAdmission CodeExecutionAdmission(string userServiceId) =>
        new(
            userServiceId,
            "chrono-sandbox",
            new AgentToolOperationIdentity.PlatformBuiltIn("code_execute"),
            AgentToolOperationAuthorizationBasis.PlatformContract,
            "POST",
            "/execute",
            "code-execution-contract-digest",
            [],
            null,
            AgentToolOperationResponsePolicy.TextOnly,
            new AgentToolOperationExecutionPolicy(
                AgentToolOperationRisk.ReadOnly,
                AgentToolOperationApproval.None,
                AgentToolOperationEnforcementOwner.Aevatar,
                [AgentToolOperationExecutionMode.Interactive,
                    AgentToolOperationExecutionMode.Durable]));

    private static void AssertFailure(
        AgentToolTerminalOutcome terminal,
        string code,
        string message)
    {
        terminal.Receipt.Should().NotBeNull();
        terminal.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        terminal.Receipt.ErrorCode.Should().Be(code);
        terminal.Receipt.ErrorMessage.Should().Be(message);
        terminal.Receipt.ResultJson.Should().Be(terminal.ResultJson);
        using var document = JsonDocument.Parse(terminal.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("error").GetString().Should().Be(code);
        root.GetProperty("code").GetString().Should().Be(code);
        root.GetProperty("message").GetString().Should().Be(message);
    }

    private static void SetSourceReadableBearer(string bearer)
    {
        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                bearer,
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
        };
    }

    private static void SetProxyDelegation(string delegation, string? sourceReadableBearer)
    {
        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                delegation,
                null,
                null,
                AgentToolNyxIdCredentialKind.ProxyDelegation,
                sourceReadableBearer),
        };
    }

    private static void SetAgentKey(string agentKey)
    {
        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                agentKey,
                null,
                null,
                AgentToolNyxIdCredentialKind.AgentKey),
        };
    }

    private static AgentToolExecutionContext SetWorkflowExecutionContext()
    {
        var context = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "workflow-bearer",
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            InvocationSurface = AgentToolInvocationSurface.WorkflowToolCall,
            OperationAdmission = CodeExecutionAdmission("us-code-admitted"),
        };
        AgentToolRequestContext.Current = context;
        return context;
    }

    private static AgentToolExecutionContext SetScheduledAgentKeyWorkflowExecutionContext(
        params NyxIdDurableOperationGrantRef[] grants)
    {
        var durableCredential = new DurableCallerCredentialRef
        {
            Ref = "sec-scheduled-agent-key",
            Purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
            OwnerScopeKey = "schedule:schedule-alpha",
            SubjectId = "key-schedule",
            ProviderCredentialId = "key-schedule",
            SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
        };
        durableCredential.NyxIdDurableOperationGrants.Add(
            grants.Select(static grant => grant.Clone()));
        var context = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "scheduled-agent-key",
                null,
                null,
                AgentToolNyxIdCredentialKind.AgentKey),
            InvocationSurface = AgentToolInvocationSurface.WorkflowToolCall,
            OperationAdmission = CodeExecutionAdmission("us-code-admitted"),
            DurableNyxIdCredential = durableCredential,
        };
        AgentToolRequestContext.Current = context;
        return context;
    }

    private static NyxIdDurableOperationGrantRef DurableGrant(
        DateTimeOffset now,
        string grantId = "grant-executions",
        DateTimeOffset? validFrom = null,
        DateTimeOffset? expiresAt = null) => new()
        {
            GrantId = grantId,
            ApiKeyId = "key-schedule",
            UserServiceId = "us-code-admitted",
            EndpointId = "endpoint-executions",
            HttpMethod = NyxIdDurableOperationHttpMethod.Post,
            NormalizedPathTemplate = "/executions",
            ContractDigest =
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ValidFromUnixMs = (validFrom ?? now.AddMinutes(-1)).ToUnixTimeMilliseconds(),
            ExpiresAtUnixMs = (expiresAt ?? now.AddDays(1)).ToUnixTimeMilliseconds(),
            ReplayPolicy = NyxIdDurableOperationReplayPolicy.DownstreamIdempotencyKey,
            ClientAuditBinding = new NyxIdDurableOperationClientAuditBinding
            {
                Platform = "lark",
                ScheduleId = "schedule-alpha",
                WorkflowRevision = "revision-7",
                CallSite = "code_execute",
            },
        };

    private static AgentToolExecutionContext SetLegacyWorkflowExecutionContext()
    {
        var context = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "workflow-bearer",
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            InvocationSurface = AgentToolInvocationSurface.WorkflowToolCall,
        };
        AgentToolRequestContext.Current = context;
        return context;
    }

    private static string OpaqueOperationId(char value) =>
        "tool:v1:operation:" + new string(value, 64);

    private static DurableCodeExecutionReceipt DurableReceipt(string providerOperationId) =>
        new(
            providerOperationId,
            $"/executions/{providerOperationId}",
            $"/executions/{providerOperationId}/result",
            $"/executions/{providerOperationId}/cancel",
            DurableCodeExecutionState.Queued,
            new CodeExecutionRouteIdentity(
                CodeExecutionContract.ServiceSlug,
                "us-code-admitted",
                CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission),
            new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 14, 12, 10, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(1));

    private static DurableCodeExecutionSnapshot DurableSnapshot(
        string providerOperationId,
        DurableCodeExecutionState state,
        DateTimeOffset expiresAt,
        bool cancelRequested = false) =>
        new(
            providerOperationId,
            state,
            state is DurableCodeExecutionState.Succeeded or DurableCodeExecutionState.Failed
                ? DurableCodeExecutionPhase.Complete
                : DurableCodeExecutionPhase.Execute,
            state is DurableCodeExecutionState.Succeeded or DurableCodeExecutionState.Failed
                ? DurableCodeExecutionCleanupState.Complete
                : DurableCodeExecutionCleanupState.NotStarted,
            2,
            cancelRequested,
            state is DurableCodeExecutionState.Succeeded or DurableCodeExecutionState.Failed,
            new CodeExecutionRouteIdentity(
                CodeExecutionContract.ServiceSlug,
                "us-code-admitted",
                CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission),
            "\"version-2\"",
            expiresAt.AddMinutes(-10),
            expiresAt.AddMinutes(-1),
            expiresAt,
            state is DurableCodeExecutionState.Succeeded or DurableCodeExecutionState.Failed
                ? expiresAt.AddMinutes(-1)
                : null,
            TimeSpan.FromSeconds(1));

    private static AgentToolPendingOperation PendingOperation(
        string operationId,
        string providerOperationId) =>
        new(
            operationId,
            providerOperationId,
            $"/executions/{providerOperationId}",
            $"/executions/{providerOperationId}/result",
            $"/executions/{providerOperationId}/cancel",
            AgentToolPendingOperationStatus.Running,
            "\"version-1\"",
            1_000,
            DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds(),
            CodeExecutionContract.ServiceSlug,
            "us-code-admitted",
            CodeExecutionRouteIdentitySource.WorkflowCapabilityAdmission);

    private sealed class StubDurableCodeExecutionPort : IDurableCodeExecutionPort
    {
        public Queue<DurableCodeExecutionSubmitOutcome> SubmitOutcomes { get; } = [];
        public Queue<DurableCodeExecutionStatusOutcome> StatusOutcomes { get; } = [];
        public Queue<DurableCodeExecutionResultOutcome> ResultOutcomes { get; } = [];
        public Queue<DurableCodeExecutionCancelOutcome> CancelOutcomes { get; } = [];
        public List<DurableCodeExecutionSubmitRequest> SubmitRequests { get; } = [];
        public List<DurableCodeExecutionOperationRequest> StatusRequests { get; } = [];
        public List<DurableCodeExecutionOperationRequest> ResultRequests { get; } = [];
        public List<DurableCodeExecutionOperationRequest> CancelRequests { get; } = [];
        public Action? BeforeSubmitResponse { get; init; }
        public Action? BeforeStatusResponse { get; init; }
        public Action? BeforeResultResponse { get; init; }

        public Task<DurableCodeExecutionSubmitOutcome> SubmitAsync(
            DurableCodeExecutionSubmitRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            SubmitRequests.Add(request);
            BeforeSubmitResponse?.Invoke();
            return Task.FromResult(SubmitOutcomes.Dequeue());
        }

        public Task<DurableCodeExecutionStatusOutcome> GetStatusAsync(
            DurableCodeExecutionOperationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StatusRequests.Add(request);
            BeforeStatusResponse?.Invoke();
            return Task.FromResult(StatusOutcomes.Dequeue());
        }

        public Task<DurableCodeExecutionResultOutcome> GetResultAsync(
            DurableCodeExecutionOperationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ResultRequests.Add(request);
            BeforeResultResponse?.Invoke();
            return Task.FromResult(ResultOutcomes.Dequeue());
        }

        public Task<DurableCodeExecutionCancelOutcome> CancelAsync(
            DurableCodeExecutionOperationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CancelRequests.Add(request);
            return Task.FromResult(CancelOutcomes.Dequeue());
        }
    }

    private sealed class StubCodeExecutionPort(CodeExecutionOutcome outcome) : ICodeExecutionPort
    {
        public CodeExecutionRequest? Request { get; private set; }

        public Task<CodeExecutionOutcome> ExecuteAsync(
            CodeExecutionRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(outcome);
        }
    }
}
