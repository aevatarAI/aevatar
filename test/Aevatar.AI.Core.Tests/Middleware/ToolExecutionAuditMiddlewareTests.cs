using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Auditing;
using Aevatar.AI.Core.Middleware;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Middleware;

public sealed class ToolExecutionAuditMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_WhenDestructiveReceiptExists_ShouldAppendTypedAuditRecord()
    {
        var appender = new RecordingAuditTrailAppender();
        var middleware = NewMiddleware(appender);
        var context = NewContext(
            new FakeAgentTool("delete_record", ToolApprovalMode.Auto, isDestructive: true, sideEffectKind: "record.delete"),
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-1", "call-1"),
                Caller = new AgentToolCallerContext("scope-1", "owner-sub-1", "response-1"),
            });

        await middleware.InvokeAsync(context, () =>
        {
            context.CredentialSource = AgentToolCredentialSource.BearerToken;
            context.Result = """{"deleted":true}""";
            context.Receipt = new AgentToolReceipt
            {
                CallId = "call-1",
                ToolName = "delete_record",
                Status = AgentToolReceiptStatus.Success,
                ApprovalMode = AgentToolReceiptApprovalMode.Auto,
                IsDestructive = true,
                SideEffectKind = "record.delete",
                SubjectKind = "record",
                SubjectId = "record-1",
                SubjectVersion = "v2",
                SubjectHash = "sha256:record",
                ResultJson = """{"deleted":true,"secret":"must-not-appear"}""",
            };
            return Task.CompletedTask;
        });

        var record = appender.Records.Should().ContainSingle().Subject;
        record.AuditActorId.Should().Be("hash:nyxid:owner-sub-1");
        record.IdentityKeyId.Should().Be("test-key");
        record.ActorKind.Should().Be(AuditActorKind.NyxidUser);
        record.CredentialSource.Should().Be(AuditCredentialSource.BearerToken);
        record.OperationKind.Should().Be(AuditOperationKind.Tool);
        record.OperationName.Should().Be("delete_record");
        record.Outcome.Should().Be(AuditOutcome.Success);
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Succeeded);
        record.CapturePlane.Should().Be(AuditCapturePlane.ToolExecution);
        record.Target.Kind.Should().Be("record");
        record.Target.Id.Should().Be("record-1");
        record.Correlation.RequestId.Should().Be("request-1");
        record.Correlation.CallId.Should().Be("call-1");
        record.Correlation.SessionId.Should().Be("response-1");
        record.Annotations.Should().Contain("side_effect_kind", "record.delete");
        record.Annotations.Should().Contain("subject_version", "v2");
        record.Annotations.Should().Contain("subject_hash", "sha256:record");
        record.Annotations.Should().Contain("receipt_synthetic", "false");
        record.RequestSummary.Should().BeEmpty();
        record.ResultSummary.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_WhenChatToolRequiresAuthorization_ShouldAuditTerminalFailureWithoutSecrets()
    {
        var appender = new RecordingAuditTrailAppender();
        var middleware = new ToolExecutionAuditMiddleware(
            appender,
            new ToolAuditRecordFactory(new OpaqueAuditActorIdentityHasher()));
        var context = NewContext(
            new FakeAgentTool("nyxid_proxy"),
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-chat", "call-chat"),
                Caller = new AgentToolCallerContext("scope-alpha", "user-audit-alpha", "turn-alpha"),
                Chat = new AgentChatInvocationContext(
                    AgentChatInvocationSurface.NyxIdAssistant,
                    "conversation-alpha",
                    "turn-alpha",
                    "task-alpha",
                    "step-alpha",
                    null),
            });
        context.ArgumentsJson = "argument-secret";

        await middleware.InvokeAsync(context, () =>
        {
            context.Result = "result-secret";
            context.Receipt = new AgentToolReceipt
            {
                CallId = "call-chat",
                ToolName = "nyxid_proxy",
                Status = AgentToolReceiptStatus.AuthorizationRequired,
                ErrorMessage = "prompt-secret",
            };
            return Task.CompletedTask;
        });

        var record = appender.Records.Should().ContainSingle().Subject;
        record.Provenance.Chat.Should().BeEquivalentTo(new AuditChatProvenance
        {
            Surface = AuditChatSurface.NyxidAssistant,
            ConversationId = "conversation-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-alpha",
        });
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Failed);
        record.Failure.Code.Should().Be("authorization_required");
        record.Failure.Category.Should().Be(AuditFailureCategory.Authorization);
        record.Redaction.OmittedFields.Should().Contain(["model.prompt", "tool.arguments", "tool.result"]);
        AuditText(record).Should().NotContain("prompt-secret")
            .And.NotContain("argument-secret")
            .And.NotContain("result-secret")
            .And.NotContain("user-audit-alpha");
    }

    [Fact]
    public async Task InvokeAsync_WhenErrorJsonReturnsWithoutReceipt_ShouldAppendUnknownAttemptAuditRecord()
    {
        var appender = new RecordingAuditTrailAppender();
        var middleware = NewMiddleware(appender);
        var context = NewContext(
            new FakeAgentTool("read_status", isReadOnly: true),
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-2", "call-2"),
                Caller = new AgentToolCallerContext("scope-2", "owner-sub-2", null),
            });

        await middleware.InvokeAsync(context, () =>
        {
            context.CredentialSource = AgentToolCredentialSource.System;
            context.Result = """{"error":true,"status":503}""";
            return Task.CompletedTask;
        });

        var record = appender.Records.Should().ContainSingle().Subject;
        record.AuditId.Should().Be("tool:request-2:call-2");
        record.Outcome.Should().Be(AuditOutcome.Unspecified);
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Running);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Unspecified);
        record.Target.Kind.Should().Be("tool");
        record.Target.Id.Should().Be("call-2");
        record.CredentialSource.Should().Be(AuditCredentialSource.System);
        record.Annotations.Should().Contain("receipt_synthetic", "true");
        record.Annotations.Should().Contain(
            "tool_receipt_status",
            AgentToolReceiptStatus.Unspecified.ToString());
        record.Annotations.Should().Contain("is_destructive", "false");
        context.Receipt.Should().NotBeNull();
        context.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Unspecified);
    }

    [Fact]
    public async Task InvokeAsync_WhenReceiptClassifierThrows_ShouldAppendUnknownAttemptAuditRecord()
    {
        var appender = new RecordingAuditTrailAppender();
        var middleware = NewMiddleware(appender);
        var context = NewContext(
            new ThrowingReceiptAgentTool(),
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-classifier", "call-classifier"),
            });

        await middleware.InvokeAsync(context, () =>
        {
            context.Result = "{\"status\":{}}";
            return Task.CompletedTask;
        });

        var record = appender.Records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Unspecified);
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Running);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Unspecified);
        record.Annotations.Should().Contain("receipt_synthetic", "true");
        context.Receipt.Should().NotBeNull();
        context.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Unspecified);
    }

    [Fact]
    public async Task InvokeAsync_WhenChannelContextIsTyped_ShouldUseChannelActorKeyAcrossRounds()
    {
        var appender = new RecordingAuditTrailAppender();
        var middleware = NewMiddleware(appender);
        var context = NewContext(
            new FakeAgentTool("channel_write", sideEffectKind: "channel.message"),
            AgentToolExecutionContext.Empty with
            {
                Caller = new AgentToolCallerContext("scope-fallback", "owner-ignored", null),
                Channel = new AgentToolChannelContext(
                    " lark ",
                    " sender-1 ",
                    " registration-scope-1 ",
                    "message-1",
                    "platform-message-1"),
            });

        await middleware.InvokeAsync(context, () =>
        {
            context.CredentialSource = AgentToolCredentialSource.ChannelRegistration;
            context.Result = """{"ok":true}""";
            return Task.CompletedTask;
        });

        var record = appender.Records.Should().ContainSingle().Subject;
        record.AuditActorId.Should().Be("hash:channel:lark:registration-scope-1:sender-1");
        record.ActorKind.Should().Be(AuditActorKind.ChannelSender);
        record.ScopeId.Should().Be("scope-fallback");
        record.CredentialSource.Should().Be(AuditCredentialSource.ChannelRegistration);
        record.Annotations.Should().Contain("channel_platform", "lark");
    }

    [Fact]
    public async Task InvokeAsync_WhenScheduledActionRuns_ShouldUseScheduleActorAndCredentialSource()
    {
        var appender = new RecordingAuditTrailAppender();
        var middleware = NewMiddleware(appender);
        var context = NewContext(
            new FakeAgentTool("scheduled_tool", sideEffectKind: "schedule.fire"),
            AgentToolExecutionContext.Empty with
            {
                Caller = new AgentToolCallerContext("scope-3", "owner-sub-3", null),
                Schedule = new AgentToolScheduleContext(" schedule-1 "),
            });

        await middleware.InvokeAsync(context, () =>
        {
            context.CredentialSource = AgentToolCredentialSource.ScheduledRun;
            context.Result = """{"ok":true}""";
            return Task.CompletedTask;
        });

        var record = appender.Records.Should().ContainSingle().Subject;
        record.AuditActorId.Should().Be("hash:schedule:schedule-1");
        record.ActorKind.Should().Be(AuditActorKind.Schedule);
        record.CredentialSource.Should().Be(AuditCredentialSource.ScheduledRun);
        record.Annotations.Should().Contain("schedule_id", "schedule-1");
    }

    [Fact]
    public async Task InvokeAsync_WhenApprovalPendingShortCircuits_ShouldAuditAcceptedOutcome()
    {
        var appender = new RecordingAuditTrailAppender();
        var middleware = NewMiddleware(appender);
        var context = NewContext(
            new FakeAgentTool("danger", ToolApprovalMode.AlwaysRequire, isDestructive: true),
            AgentToolExecutionContext.Empty with
            {
                Caller = new AgentToolCallerContext("scope-4", "owner-sub-4", "session-4"),
            });

        await middleware.InvokeAsync(context, () =>
        {
            context.CredentialSource = AgentToolCredentialSource.BearerToken;
            context.Terminate = true;
            context.TerminationKind = ToolCallTerminationKind.ApprovalPending;
            context.PendingApproval = new ToolApprovalPendingContext(
                "approval-1",
                "danger",
                "call-1",
                "{}",
                ToolApprovalMode.AlwaysRequire,
                IsReadOnly: false,
                IsDestructive: true);
            context.Receipt = new AgentToolReceipt
            {
                CallId = "call-1",
                ToolName = "danger",
                Status = AgentToolReceiptStatus.ApprovalRequired,
                ApprovalMode = AgentToolReceiptApprovalMode.AlwaysRequire,
                IsDestructive = true,
                ApprovalRequestId = "approval-1",
            };
            return Task.CompletedTask;
        });

        var record = appender.Records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Accepted);
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.WaitingApproval);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Unspecified);
        record.Correlation.ApprovalId.Should().Be("approval-1");
        record.Annotations.Should().Contain("tool_receipt_status", AgentToolReceiptStatus.ApprovalRequired.ToString());
    }

    [Fact]
    public async Task InvokeAsync_WhenCredentialPolicyShortCircuitsWithoutReceipt_ShouldAuditDeniedOutcome()
    {
        var appender = new RecordingAuditTrailAppender();
        var middleware = NewMiddleware(appender);
        var context = NewContext(
            new FakeAgentTool("mutate", isDestructive: true),
            AgentToolExecutionContext.Empty with
            {
                Caller = new AgentToolCallerContext("scope-5", "owner-sub-5", null),
                Channel = new AgentToolChannelContext("lark", "sender-5", "registration-5", null, null),
            });

        await middleware.InvokeAsync(context, () =>
        {
            context.CredentialSource = AgentToolCredentialSource.ChannelRegistration;
            context.Terminate = true;
            context.TerminationKind = ToolCallTerminationKind.MiddlewareTerminated;
            context.TerminationReason = "sender is not bound: Bearer secret-token";
            context.Result = """{"error":"credential_denied","code":"credential_denied","token":"secret-token"}""";
            return Task.CompletedTask;
        });

        var record = appender.Records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Denied);
        record.ErrorCode.Should().Be("credential_denied");
        record.ErrorSummary.Should().Be("credential_denied");
        record.Failure.SanitizedMessage.Should().Be("credential_denied");
        record.Annotations.Should().Contain("receipt_synthetic", "true");
        AuditText(record).Should().NotContain("secret-token");
    }

    [Fact]
    public async Task InvokeAsync_WhenApprovalIsDenied_ShouldRecordWaitingApprovalAsFailedPhase()
    {
        var appender = new RecordingAuditTrailAppender();
        var middleware = NewMiddleware(appender);
        var context = NewContext(
            new FakeAgentTool("danger", ToolApprovalMode.AlwaysRequire, isDestructive: true),
            AgentToolExecutionContext.Empty with
            {
                Caller = new AgentToolCallerContext("scope-approval", "owner-approval", null),
            });

        await middleware.InvokeAsync(context, () =>
        {
            context.Receipt = new AgentToolReceipt
            {
                CallId = "call-approval",
                ToolName = "danger",
                Status = AgentToolReceiptStatus.Denied,
                ApprovalMode = AgentToolReceiptApprovalMode.AlwaysRequire,
                ApprovalRequestId = "approval-denied-1",
                ErrorCode = "approval_denied",
                ErrorMessage = "approval_denied",
                IsDestructive = true,
            };
            return Task.CompletedTask;
        });

        var record = appender.Records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Denied);
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Failed);
        record.Failure.FailedPhase.Should().Be(AuditLifecyclePhase.WaitingApproval);
        record.Correlation.ApprovalId.Should().Be("approval-denied-1");
    }

    [Fact]
    public async Task InvokeAsync_WhenApprovalTimesOut_ShouldRecordTimedOutTerminalOutcome()
    {
        const string secret = "must-not-be-recorded";
        var appender = new RecordingAuditTrailAppender();
        var middleware = NewMiddleware(appender);
        var context = NewContext(
            new FakeAgentTool("danger", ToolApprovalMode.AlwaysRequire, isDestructive: true),
            AgentToolExecutionContext.Empty with
            {
                Caller = new AgentToolCallerContext("scope-timeout", "owner-timeout", null),
            });

        await middleware.InvokeAsync(context, () =>
        {
            context.Terminate = true;
            context.TerminationKind = ToolCallTerminationKind.ApprovalTimedOut;
            context.TerminationReason = $"Approval timed out: {secret}";
            context.PendingApproval = new ToolApprovalPendingContext(
                "approval-timeout-1",
                "danger",
                "call-timeout",
                "{}",
                ToolApprovalMode.AlwaysRequire,
                IsReadOnly: false,
                IsDestructive: true);
            return Task.CompletedTask;
        });

        var record = appender.Records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Error);
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.TimedOut);
        record.Failure.Code.Should().Be("approval_timeout");
        record.Failure.Category.Should().Be(AuditFailureCategory.Timeout);
        record.Failure.FailedPhase.Should().Be(AuditLifecyclePhase.WaitingApproval);
        record.Correlation.ApprovalId.Should().Be("approval-timeout-1");
        AuditText(record).Should().NotContain(secret);
    }

    [Fact]
    public async Task InvokeAsync_WhenReceiptErrorFieldsContainCompactSecrets_ShouldUseOwnedFailureCode()
    {
        const string compactSecret = "NYXID_PROXY_HTTP_502_compactSecretToken123";
        var appender = new RecordingAuditTrailAppender();
        var middleware = NewMiddleware(appender);
        var context = NewContext(
            new FakeAgentTool("failing_tool"),
            AgentToolExecutionContext.Empty with
            {
                Caller = new AgentToolCallerContext("scope-error", "owner-error", null),
            });

        await middleware.InvokeAsync(context, () =>
        {
            context.Receipt = new AgentToolReceipt
            {
                CallId = "call-error",
                ToolName = "failing_tool",
                Status = AgentToolReceiptStatus.Error,
                ErrorCode = compactSecret,
                ErrorMessage = compactSecret,
            };
            return Task.CompletedTask;
        });

        var record = appender.Records.Should().ContainSingle().Subject;
        record.ErrorCode.Should().Be("tool_error");
        record.ErrorSummary.Should().Be("tool_error");
        record.Failure.Code.Should().Be("tool_error");
        record.Failure.SanitizedMessage.Should().Be("tool_error");
        AuditText(record).Should().NotContain(compactSecret);
    }

    [Theory]
    [InlineData("NYXID_PROXY_HTTP_502")]
    [InlineData("NYXID_PROXY_UNAUTHORIZED")]
    [InlineData("NYXID_PROXY_FORBIDDEN")]
    public async Task InvokeAsync_WhenReceiptUsesOwnedNyxIdProxyFailureCode_ShouldPreserveIt(
        string failureCode)
    {
        var appender = new RecordingAuditTrailAppender();
        var middleware = NewMiddleware(appender);
        var context = NewContext(
            new FakeAgentTool("nyxid_proxy"),
            AgentToolExecutionContext.Empty with
            {
                Caller = new AgentToolCallerContext("scope-nyxid", "owner-nyxid", null),
            });

        await middleware.InvokeAsync(context, () =>
        {
            context.Receipt = new AgentToolReceipt
            {
                CallId = "call-nyxid",
                ToolName = "nyxid_proxy",
                Status = AgentToolReceiptStatus.Error,
                SubjectKind = "nyxid.user-service",
                SubjectId = "us-home-alpha",
                ErrorCode = failureCode,
                ErrorMessage = "provider-secret-must-not-appear",
            };
            return Task.CompletedTask;
        });

        var record = appender.Records.Should().ContainSingle().Subject;
        record.ErrorCode.Should().Be(failureCode);
        record.Failure.Code.Should().Be(failureCode);
        record.Target.Kind.Should().Be("nyxid.user-service");
        record.Target.Id.Should().Be("us-home-alpha");
        AuditText(record).Should().NotContain("provider-secret-must-not-appear");
    }

    [Theory]
    [InlineData("WEB_FETCH_HTTP_503")]
    [InlineData("WEB_FETCH_DNS_FAILURE")]
    [InlineData("WEB_FETCH_TLS_FAILURE")]
    [InlineData("WEB_FETCH_TIMEOUT")]
    public async Task InvokeAsync_WhenReceiptUsesOwnedWebFetchFailureCode_ShouldPreserveIt(
        string failureCode)
    {
        var appender = new RecordingAuditTrailAppender();
        var middleware = NewMiddleware(appender);
        var context = NewContext(
            new FakeAgentTool("web_fetch", isReadOnly: true),
            AgentToolExecutionContext.Empty with
            {
                Caller = new AgentToolCallerContext("scope-web", "owner-web", null),
            });

        await middleware.InvokeAsync(context, () =>
        {
            context.Receipt = new AgentToolReceipt
            {
                CallId = "call-web",
                ToolName = "web_fetch",
                Status = AgentToolReceiptStatus.Error,
                ErrorCode = failureCode,
                ErrorMessage = "The web fetch failed.",
            };
            return Task.CompletedTask;
        });

        var record = appender.Records.Should().ContainSingle().Subject;
        record.ErrorCode.Should().Be(failureCode);
        record.Failure.Code.Should().Be(failureCode);
        record.TerminalOutcome.Should().Be(
            failureCode == "WEB_FETCH_TIMEOUT"
                ? AuditTerminalOutcome.TimedOut
                : AuditTerminalOutcome.Failed);
    }

    [Fact]
    public async Task InvokeAsync_WhenCodeExecuteReturnsTypedFailure_ShouldPreserveFailureCode()
    {
        var appender = new RecordingAuditTrailAppender();
        var middleware = NewMiddleware(appender);
        var context = NewContext(
            new FakeAgentTool("code_execute"),
            AgentToolExecutionContext.Empty with
            {
                Caller = new AgentToolCallerContext("scope-code", "owner-code", null),
            });

        await middleware.InvokeAsync(context, () =>
        {
            context.Receipt = new AgentToolReceipt
            {
                CallId = "call-code",
                ToolName = "code_execute",
                Status = AgentToolReceiptStatus.Error,
                ErrorCode = "CODE_EXECUTE_FAILED",
                ErrorMessage = "Code execution failed.",
            };
            return Task.CompletedTask;
        });

        var record = appender.Records.Should().ContainSingle().Subject;
        record.ErrorCode.Should().Be("CODE_EXECUTE_FAILED");
        record.Failure.Code.Should().Be("CODE_EXECUTE_FAILED");
    }

    [Fact]
    public async Task InvokeAsync_ShouldNotRecordFullArgumentsResultsTokensOrReceiptResultJson()
    {
        var appender = new RecordingAuditTrailAppender();
        var middleware = NewMiddleware(appender);
        var context = NewContext(
            new FakeAgentTool("token_tool", sideEffectKind: "token.test"),
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-token", "call-token"),
                Credentials = new AgentToolCredentials("sentinel-access-token", "sentinel-org-token", null),
                Caller = new AgentToolCallerContext("scope-token", "owner-token", null),
            });
        context.ArgumentsJson = """{"api_key":"sentinel-argument-token"}""";

        await middleware.InvokeAsync(context, () =>
        {
            context.CredentialSource = AgentToolCredentialSource.BearerToken;
            context.Result = """{"result":"sentinel-result-token"}""";
            context.Receipt = new AgentToolReceipt
            {
                CallId = "call-token",
                ToolName = "token_tool",
                Status = AgentToolReceiptStatus.Success,
                SideEffectKind = "token.test",
                ResultJson = """{"secret":"sentinel-receipt-token"}""",
            };
            return Task.CompletedTask;
        });

        var record = appender.Records.Should().ContainSingle().Subject;
        record.RequestSummary.Should().BeEmpty();
        record.ResultSummary.Should().BeEmpty();
        var text = AuditText(record);
        text.Should().NotContain("sentinel-access-token");
        text.Should().NotContain("sentinel-org-token");
        text.Should().NotContain("sentinel-argument-token");
        text.Should().NotContain("sentinel-result-token");
        text.Should().NotContain("sentinel-receipt-token");
    }

    [Fact]
    public async Task InvokeAsync_WhenAppenderFails_ShouldPreserveToolResult()
    {
        var appender = new RecordingAuditTrailAppender { ThrowOnAppend = true };
        var middleware = NewMiddleware(appender);
        var context = NewContext(
            new FakeAgentTool("safe_tool", isReadOnly: true),
            AgentToolExecutionContext.Empty with
            {
                Caller = new AgentToolCallerContext("scope-6", "owner-sub-6", null),
            });

        await middleware.InvokeAsync(context, () =>
        {
            context.Result = """{"ok":true}""";
            return Task.CompletedTask;
        });

        context.Result.Should().Be("""{"ok":true}""");
        appender.Attempts.Should().Be(1);
        appender.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_WhenToolExecutionThrows_ShouldAppendSyntheticErrorAuditRecordAndRethrow()
    {
        var appender = new RecordingAuditTrailAppender();
        var middleware = NewMiddleware(appender);
        var context = NewContext(
            new FakeAgentTool("throwing_tool", isDestructive: true, sideEffectKind: "tool.throw"),
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-error", "call-error"),
                Caller = new AgentToolCallerContext("scope-error", "owner-error", "session-error"),
            });
        const string secret = "provider-secret-token";
        var exception = new InvalidOperationException($"tool exploded with Authorization: Bearer {secret}");

        var act = async () => await middleware.InvokeAsync(context, () =>
        {
            context.CredentialSource = AgentToolCredentialSource.BearerToken;
            throw exception;
        });

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Should().BeSameAs(exception);
        var record = appender.Records.Should().ContainSingle().Subject;
        record.Outcome.Should().Be(AuditOutcome.Error);
        record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
        record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Failed);
        record.Failure.Category.Should().Be(AuditFailureCategory.Execution);
        record.ErrorCode.Should().Be("tool_execution_exception");
        record.ErrorSummary.Should().Be("tool_execution_exception");
        AuditText(record).Should().NotContain(secret);
        record.CredentialSource.Should().Be(AuditCredentialSource.BearerToken);
        record.Target.Kind.Should().Be("tool");
        record.Target.Id.Should().Be("call-error");
        record.Correlation.RequestId.Should().Be("request-error");
        record.Correlation.CallId.Should().Be("call-error");
        record.Annotations.Should().Contain("receipt_synthetic", "true");
        record.Annotations.Should().Contain("tool_receipt_status", AgentToolReceiptStatus.Error.ToString());
        record.Annotations.Should().Contain("side_effect_kind", "tool.throw");
    }

    private static ToolExecutionAuditMiddleware NewMiddleware(RecordingAuditTrailAppender appender) =>
        new(appender, new ToolAuditRecordFactory(new StableAuditActorIdentityHasher()));

    private static ToolCallContext NewContext(IAgentTool tool, AgentToolExecutionContext executionContext) =>
        new()
        {
            Tool = tool,
            ToolName = tool.Name,
            ToolCallId = executionContext.Request.CallId ?? "call-1",
            ArgumentsJson = "{}",
            ExecutionContext = executionContext,
        };

    private static string AuditText(AuditRecord record) =>
        string.Join(
            '\n',
            [
                record.AuditId,
                record.ScopeId,
                record.AuditActorId,
                record.IdentityKeyId,
                record.OperationName,
                record.RequestSummary,
                record.ResultSummary,
                record.ErrorCode,
                record.ErrorSummary,
                record.Target.Kind,
                record.Target.Id,
                record.Target.DisplayName,
                record.Correlation.TraceId,
                record.Correlation.RequestId,
                record.Correlation.CommandId,
                record.Correlation.CallId,
                record.Correlation.SessionId,
                record.Correlation.WorkflowRunId,
                record.Correlation.ApprovalId,
                .. record.Annotations.Select(pair => $"{pair.Key}={pair.Value}"),
            ]);

    private sealed class RecordingAuditTrailAppender : IAuditTrailAppender
    {
        public List<AuditRecord> Records { get; } = [];

        public int Attempts { get; private set; }

        public bool ThrowOnAppend { get; init; }

        public Task<AuditTrailAppendResult> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (ThrowOnAppend)
                throw new InvalidOperationException("audit store unavailable");

            Records.Add(record);
            return Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId, record.AuditActorId, DateTimeOffset.UtcNow));
        }
    }

    private sealed class StableAuditActorIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) =>
            new($"hash:{canonicalActorKey}", "test-key");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) =>
            string.Equals(auditActorId, $"hash:{canonicalActorKey}", StringComparison.Ordinal) &&
            string.Equals(identityKeyId, "test-key", StringComparison.Ordinal);
    }

    private sealed class OpaqueAuditActorIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) =>
            new("audit_actor:hmac-sha256:opaque", "test-key");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) =>
            string.Equals(auditActorId, "audit_actor:hmac-sha256:opaque", StringComparison.Ordinal) &&
            string.Equals(identityKeyId, "test-key", StringComparison.Ordinal);
    }

    private sealed class FakeAgentTool(
        string name,
        ToolApprovalMode approvalMode = ToolApprovalMode.NeverRequire,
        bool isReadOnly = false,
        bool isDestructive = false,
        string sideEffectKind = "") : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode { get; } = approvalMode;
        public bool IsReadOnly { get; } = isReadOnly;
        public bool IsDestructive { get; } = isDestructive;
        public string SideEffectKind { get; } = sideEffectKind;
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) => Task.FromResult("{}");
    }

    private sealed class ThrowingReceiptAgentTool : IAgentTool
    {
        public string Name => "throwing_receipt_classifier";
        public string Description => "fake";
        public string ParametersSchema => "{}";
        public bool IsReadOnly => true;

        public AgentToolReceipt? CreateResultReceipt(
            string callId,
            string toolName,
            string argumentsJson,
            string resultJson) =>
            throw new InvalidOperationException("Malformed provider result.");

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }
}
