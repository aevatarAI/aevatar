using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Audit.Core.Projection;
using Aevatar.Audit.Core.Stores;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Aevatar.Audit.Core.Tests;

public sealed class InMemoryAuditTrailStoreTests
{
    [Fact]
    public async Task AppendAsync_StoresCloneAndReturnsReceipt()
    {
        var store = new InMemoryAuditTrailStore();
        var record = CreateRecord("audit-1", "scope-a", "actor-a", "api.call", AuditOutcome.Success);

        var receipt = await store.AppendAsync(record);
        record.ScopeId = "mutated";

        var page = await store.QueryAsync(new AuditTrailQuery { Take = 10 });

        receipt.AuditId.ShouldBe("audit-1");
        receipt.AuditActorId.ShouldBe("actor-a");
        page.Records.Single().ScopeId.ShouldBe("scope-a");
        page.Records.Single().ShouldNotBeSameAs(record);
    }

    [Fact]
    public async Task AppendAsync_WhenSameAuditAndContentExists_ShouldReturnDuplicateWithoutAddingRecord()
    {
        var store = new InMemoryAuditTrailStore();
        var record = CreateRecord("audit-1", "scope-a", "actor-a", "api.call", AuditOutcome.Success);
        var retry = record.Clone();
        retry.RecordedAt = Timestamp.FromDateTimeOffset(
            record.RecordedAt.ToDateTimeOffset().AddMinutes(1));

        var first = await store.AppendAsync(record);
        var second = await store.AppendAsync(retry);
        var page = await store.QueryAsync(new AuditTrailQuery { Take = 10 });

        first.Status.ShouldBe(AuditTrailAppendStatus.Appended);
        second.Status.ShouldBe(AuditTrailAppendStatus.Duplicate);
        page.Records.Select(static item => item.AuditId).ShouldBe(["audit-1"]);
        page.Records.Single().RecordedAt.ShouldBe(record.RecordedAt);
    }

    [Fact]
    public async Task AppendAsync_WhenSameAuditAndDifferentContentExists_ShouldReturnConflictWithoutReplacingRecord()
    {
        var store = new InMemoryAuditTrailStore();
        var original = CreateRecord("audit-1", "scope-a", "actor-a", "api.call", AuditOutcome.Success);
        var conflicting = original.Clone();
        conflicting.RequestSummary = "different request";

        await store.AppendAsync(original);
        var result = await store.AppendAsync(conflicting);
        var page = await store.QueryAsync(new AuditTrailQuery { Take = 10 });

        result.Status.ShouldBe(AuditTrailAppendStatus.Conflict);
        page.Records.ShouldHaveSingleItem().RequestSummary.ShouldBe("request summary");
    }

    [Fact]
    public async Task QueryAsync_FiltersBySemanticFieldsAndCorrelation()
    {
        var store = new InMemoryAuditTrailStore();
        await store.AppendManyAsync(
        [
            CreateRecord("audit-1", "scope-a", "actor-a", "api.call", AuditOutcome.Success),
            CreateRecord("audit-2", "scope-a", "actor-b", "tool.call", AuditOutcome.Denied),
            CreateRecord("audit-3", "scope-b", "actor-a", "api.call", AuditOutcome.Error)
        ]);

        var page = await store.QueryAsync(new AuditTrailQuery
        {
            ScopeId = "scope-a",
            AuditActorId = "actor-b",
            OperationKind = AuditOperationKind.Tool,
            OperationName = "tool.call",
            Outcome = AuditOutcome.Denied,
            SensitivityLevel = AuditSensitivityLevel.Confidential,
            CapturePlane = AuditCapturePlane.ToolExecution,
            TargetKind = "workflow",
            TargetId = "wf-audit-2",
            RequestId = "req-audit-2",
            WorkflowRunId = "run-audit-2",
            CommittedEventId = "event-audit-2",
            CommittedActorId = "actor-ref-audit-2",
            CommittedActorType = "WorkflowRunGAgent",
            CommittedEventTypeUrl = "type.googleapis.com/aevatar.audit.TestEvent",
            CommittedStateVersion = 20,
            Take = 10
        });

        page.Records.Select(static record => record.AuditId).ShouldBe(["audit-2"]);
        page.NextCursor.ShouldBeNull();
        page.Coverage.IngestionWatermark.ShouldNotBeNull();
        page.Coverage.SchemaCompatibility.ShouldBe(AuditSchemaCompatibility.Current);
    }

    [Fact]
    public async Task QueryAsync_FiltersChatActivityBeforePaginationAcrossRetainedActorKeys()
    {
        var store = new InMemoryAuditTrailStore();
        await store.AppendManyAsync(
        [
            CreateRecord("audit-non-chat", "scope-alpha", "actor-key-2", "tool.call", AuditOutcome.Error, seconds: 10),
            WithChat(CreateRecord("audit-other-user", "scope-alpha", "actor-beta", "tool.call", AuditOutcome.Error, seconds: 9)),
            WithChat(CreateRecord("audit-workflow", "scope-alpha", "actor-key-2", "tool.call", AuditOutcome.Error, seconds: 8), AuditChatSurface.WorkflowChat),
            WithChat(CreateRecord("audit-other-conversation", "scope-alpha", "actor-key-2", "tool.call", AuditOutcome.Error, seconds: 7), conversationId: "conversation-beta"),
            WithChat(CreateRecord("audit-success", "scope-alpha", "actor-key-2", "tool.call", AuditOutcome.Success, seconds: 6)),
            WithChat(CreateRecord("audit-current-key", "scope-alpha", "actor-key-2", "tool.call", AuditOutcome.Error, seconds: 5)),
            WithChat(CreateRecord("audit-retained-key", "scope-alpha", "actor-key-1", "tool.call", AuditOutcome.Error, seconds: 4))
        ]);

        var query = new AuditTrailQuery
        {
            ScopeId = "scope-alpha",
            AuditActorIds = [" actor-key-2 ", "actor-key-1"],
            RequireChatProvenance = true,
            ChatSurface = AuditChatSurface.NyxidAssistant,
            ChatConversationId = "conversation-alpha",
            TerminalOutcome = AuditTerminalOutcome.Failed,
            Take = 1,
        };

        var first = await store.QueryAsync(query);
        var second = await store.QueryAsync(query with { Cursor = first.NextCursor });

        first.Records.Select(static record => record.AuditId).ShouldBe(["audit-current-key"]);
        first.NextCursor.ShouldNotBeNull();
        second.Records.Select(static record => record.AuditId).ShouldBe(["audit-retained-key"]);
        second.NextCursor.ShouldBeNull();
    }

    [Theory]
    [InlineData("occurred_from")]
    [InlineData("occurred_to")]
    [InlineData("scope_id")]
    [InlineData("audit_actor_id")]
    [InlineData("actor_kind")]
    [InlineData("identity_key_id")]
    [InlineData("operation_name")]
    [InlineData("operation_kind")]
    [InlineData("outcome")]
    [InlineData("lifecycle_phase")]
    [InlineData("terminal_outcome")]
    [InlineData("sensitivity_level")]
    [InlineData("capture_plane")]
    [InlineData("target_kind")]
    [InlineData("target_id")]
    [InlineData("trace_id")]
    [InlineData("correlation_id")]
    [InlineData("causation_id")]
    [InlineData("request_id")]
    [InlineData("command_id")]
    [InlineData("call_id")]
    [InlineData("session_id")]
    [InlineData("workflow_run_id")]
    [InlineData("approval_id")]
    [InlineData("committed_event_id")]
    [InlineData("committed_actor_id")]
    [InlineData("committed_actor_type")]
    [InlineData("committed_event_type_url")]
    [InlineData("committed_state_version")]
    public async Task QueryAsync_EachFilterPredicateExcludesSingleFieldDecoy(string filterName)
    {
        var store = new InMemoryAuditTrailStore();
        var matchingRecord = CreateFocusedRecord("audit-match");
        var decoyRecord = matchingRecord.Clone();
        decoyRecord.AuditId = "audit-decoy";

        var query = BuildSingleFilterQuery(filterName, decoyRecord);

        await store.AppendManyAsync([matchingRecord, decoyRecord]);

        var page = await store.QueryAsync(query);

        page.Records.Select(static record => record.AuditId).ShouldBe(["audit-match"]);
    }

    [Fact]
    public async Task QueryAsync_PaginatesNewestFirstAndKeepsMatchingWatermark()
    {
        var store = new InMemoryAuditTrailStore();
        await store.AppendManyAsync(
        [
            CreateRecord("audit-3", "scope-a", "actor-a", "api.call", AuditOutcome.Success, seconds: 3),
            CreateRecord("audit-1", "scope-a", "actor-a", "api.call", AuditOutcome.Success, seconds: 1),
            CreateRecord("audit-2", "scope-a", "actor-a", "api.call", AuditOutcome.Success, seconds: 2),
            CreateRecord("audit-other-scope", "scope-b", "actor-a", "api.call", AuditOutcome.Success, seconds: 10)
        ]);

        var first = await store.QueryAsync(new AuditTrailQuery { ScopeId = "scope-a", Take = 2 });
        var second = await store.QueryAsync(new AuditTrailQuery { ScopeId = "scope-a", Cursor = first.NextCursor, Take = 2 });

        first.Records.Select(static record => record.AuditId).ShouldBe(["audit-3", "audit-2"]);
        first.NextCursor.ShouldNotBeNull();
        first.Coverage.IngestionWatermark.ShouldBe(DateTimeOffset.Parse("2026-01-02T03:04:15Z"));
        first.Coverage.Truncated.ShouldBeTrue();
        second.Records.Select(static record => record.AuditId).ShouldBe(["audit-1"]);
        second.NextCursor.ShouldBeNull();
        second.Coverage.IngestionWatermark.ShouldBe(first.Coverage.IngestionWatermark);
        second.Coverage.Truncated.ShouldBeFalse();
    }

    [Fact]
    public async Task QueryAsync_WhenBoundedWindowExtendsPastIngestionWatermark_ReportsBehindWatermark()
    {
        var store = new InMemoryAuditTrailStore();
        await store.AppendAsync(
            CreateRecord("audit-1", "scope-a", "actor-a", "api.call", AuditOutcome.Success));
        var from = DateTimeOffset.Parse("2026-01-02T03:04:00Z");
        var to = DateTimeOffset.Parse("2026-01-02T03:04:10Z");

        var page = await store.QueryAsync(new AuditTrailQuery
        {
            OccurredFrom = from,
            OccurredTo = to,
            Take = 10,
        });

        page.Coverage.RequestedWindow.ShouldBe(new AuditQueryWindow(from, to));
        page.Coverage.EffectiveWindow.ShouldBe(new AuditQueryWindow(from, to));
        page.Coverage.IngestionWatermark.ShouldBe(DateTimeOffset.Parse("2026-01-02T03:04:05Z"));
        page.Coverage.CompleteThrough.ShouldBeNull();
        page.Coverage.WindowCompleteness.ShouldBe(AuditWindowCompleteness.BehindIngestionWatermark);
    }

    [Fact]
    public async Task UpsertAsync_ShouldApplyArtifactAndExposeCloneToQueryAndGet()
    {
        var store = new InMemoryAuditTrailStore();
        var document = CreateDocument("audit-1");
        document.Record.RequestSummary = "  request   summary  ";

        var result = await store.UpsertAsync(document);
        document.ScopeId = "mutated";
        document.Record.ScopeId = "mutated";

        var saved = await store.GetAsync("audit-1");
        saved!.ScopeId = "changed-after-read";
        var reread = await store.GetAsync("audit-1");
        var page = await store.QueryAsync(new AuditTrailQuery { ScopeId = "scope-a", Take = 10 });

        result.Disposition.ShouldBe(AuditTrailArtifactWriteDisposition.Applied);
        saved.ShouldNotBeSameAs(document);
        saved.Record.ScopeId.ShouldBe("scope-a");
        saved.Record.RequestSummary.ShouldBe("request summary");
        saved.ContentHash.ShouldNotBe(document.ContentHash);
        reread!.ScopeId.ShouldBe("scope-a");
        page.Records.ShouldHaveSingleItem().RequestSummary.ShouldBe("request summary");
    }

    [Fact]
    public async Task UpsertAsync_WhenSameSanitizedContentHasDifferentCallerHash_ShouldReturnDuplicate()
    {
        var store = new InMemoryAuditTrailStore();
        var document = CreateDocument("audit-1", "caller-hash-a");
        var duplicate = CreateDocument("audit-1", "caller-hash-b");

        var first = await store.UpsertAsync(document);
        var second = await store.UpsertAsync(duplicate);
        var page = await store.QueryAsync(new AuditTrailQuery { Take = 10 });

        first.Disposition.ShouldBe(AuditTrailArtifactWriteDisposition.Applied);
        second.Disposition.ShouldBe(AuditTrailArtifactWriteDisposition.Duplicate);
        page.Records.Select(static record => record.AuditId).ShouldBe(["audit-1"]);
    }

    [Fact]
    public async Task UpsertAsync_WhenSameAuditAndDifferentSanitizedContentExists_ShouldReturnConflict()
    {
        var store = new InMemoryAuditTrailStore();
        var original = CreateDocument("audit-1", "caller-hash-a");
        var conflicting = CreateDocument("audit-1", "caller-hash-b");
        conflicting.Record.RequestSummary = "different request";
        await store.UpsertAsync(original);

        var result = await store.UpsertAsync(conflicting);
        var saved = await store.GetAsync("audit-1");
        var page = await store.QueryAsync(new AuditTrailQuery { Take = 10 });

        result.Disposition.ShouldBe(AuditTrailArtifactWriteDisposition.Conflict);
        saved!.ContentHash.ShouldNotBe(original.ContentHash);
        saved.Record.RequestSummary.ShouldBe("request summary");
        page.Records.Select(static record => record.AuditId).ShouldBe(["audit-1"]);
    }

    [Fact]
    public async Task ArtifactStoreMethods_ShouldObserveCancelledToken()
    {
        var store = new InMemoryAuditTrailStore();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => store.GetAsync("audit-1", cts.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => store.UpsertAsync(CreateDocument("audit-1"), cts.Token));
    }

    private static AuditRecord CreateRecord(
        string auditId,
        string scopeId,
        string auditActorId,
        string operationName,
        AuditOutcome outcome,
        int seconds = 0)
    {
        var occurredAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z").AddSeconds(seconds);
        var terminalOutcome = outcome switch
        {
            AuditOutcome.Success => AuditTerminalOutcome.Succeeded,
            AuditOutcome.Cancelled => AuditTerminalOutcome.Cancelled,
            AuditOutcome.Accepted => AuditTerminalOutcome.Unspecified,
            _ => AuditTerminalOutcome.Failed,
        };
        var record = new AuditRecord
        {
            AuditId = auditId,
            OccurredAt = Timestamp.FromDateTimeOffset(occurredAt),
            RecordedAt = Timestamp.FromDateTimeOffset(occurredAt),
            EventKind = operationName,
            Subject = $"workflow/wf-{auditId}",
            SchemaVersion = "1.0",
            Source = "urn:aevatar:audit:test",
            ScopeId = scopeId,
            AuditActorId = auditActorId,
            IdentityKeyId = "key-1",
            ActorKind = AuditActorKind.NyxidUser,
            CredentialSource = AuditCredentialSource.NyxidAssertion,
            OperationKind = operationName.StartsWith("tool.", StringComparison.Ordinal) ? AuditOperationKind.Tool : AuditOperationKind.Api,
            OperationName = operationName,
            SensitivityLevel = AuditSensitivityLevel.Confidential,
            Outcome = outcome,
            LifecyclePhase = outcome == AuditOutcome.Accepted
                ? AuditLifecyclePhase.Accepted
                : AuditLifecyclePhase.Terminal,
            TerminalOutcome = terminalOutcome,
            CapturePlane = operationName.StartsWith("tool.", StringComparison.Ordinal)
                ? AuditCapturePlane.ToolExecution
                : AuditCapturePlane.BoundaryEndpoint,
            Target = new AuditTarget { Kind = "workflow", Id = $"wf-{auditId}" },
            Correlation = new AuditCorrelation
            {
                TraceId = $"trace-{auditId}",
                CorrelationId = $"correlation-{auditId}",
                CausationId = $"causation-{auditId}",
                RequestId = $"req-{auditId}",
                CommandId = $"cmd-{auditId}",
                CallId = $"call-{auditId}",
                SessionId = $"session-{auditId}",
                WorkflowRunId = $"run-{auditId}",
                ApprovalId = $"approval-{auditId}"
            },
            CommittedFactRef = new AuditCommittedFactReference
            {
                CommittedEventId = $"event-{auditId}",
                ActorId = $"actor-ref-{auditId}",
                ActorType = "WorkflowRunGAgent",
                EventTypeUrl = "type.googleapis.com/aevatar.audit.TestEvent",
                StateVersion = seconds == 0 ? 20 : seconds
            },
            RequestSummary = "request summary",
            ResultSummary = "result summary",
            Provenance = new AuditExecutionProvenance
            {
                ScopeId = scopeId,
                RunId = $"run-{auditId}",
                ActorId = $"actor-ref-{auditId}",
                ActorStateVersion = seconds == 0 ? 20 : seconds,
                ActorEventId = $"event-{auditId}",
            },
            Redaction = new AuditRedaction
            {
                Policy = "aevatar.audit.test.v1",
                ValuesSanitized = true,
            },
        };
        if (terminalOutcome == AuditTerminalOutcome.Failed)
        {
            record.Failure = new AuditFailure
            {
                Code = "test_failed",
                Category = AuditFailureCategory.Execution,
                Retryability = AuditRetryability.Unknown,
                FailedPhase = AuditLifecyclePhase.Running,
                SanitizedMessage = "Test execution failed.",
            };
        }

        return record;
    }

    private static AuditRecord WithChat(
        AuditRecord record,
        AuditChatSurface surface = AuditChatSurface.NyxidAssistant,
        string conversationId = "conversation-alpha")
    {
        record.Provenance.Chat = new AuditChatProvenance
        {
            Surface = surface,
            ConversationId = conversationId,
            TurnId = "turn-alpha",
        };
        return record;
    }

    private static AuditTrailDocument CreateDocument(string auditId, string contentHash = "content-a") =>
        new()
        {
            Id = auditId,
            AuditId = auditId,
            ContentHash = contentHash,
            Record = CreateRecord(auditId, "scope-a", "actor-a", "api.call", AuditOutcome.Success),
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-01-02T03:04:05Z")),
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-01-02T03:05:05Z")),
            ScopeId = "scope-a",
            AuditActorId = "actor-a",
            OperationName = "api.call",
            Outcome = AuditOutcome.Success,
            SensitivityLevel = AuditSensitivityLevel.Confidential,
            TargetKind = "workflow",
            TargetId = $"wf-{auditId}",
            RequestId = $"req-{auditId}",
            CommandId = $"cmd-{auditId}",
            CorrelationId = $"correlation-{auditId}",
            SessionId = $"session-{auditId}",
            WorkflowRunId = $"run-{auditId}",
            CommittedEventId = $"event-{auditId}",
            CommittedActorId = $"actor-ref-{auditId}",
            CommittedActorType = "WorkflowRunGAgent",
            CommittedEventTypeUrl = "type.googleapis.com/aevatar.audit.TestEvent",
            CommittedStateVersion = 20,
        };

    private static AuditRecord CreateFocusedRecord(string auditId)
    {
        var record = CreateRecord(auditId, "scope-match", "actor-match", "tool.match", AuditOutcome.Denied);
        record.IdentityKeyId = "key-match";
        record.Target = new AuditTarget { Kind = "workflow", Id = "workflow-match" };
        record.Subject = "workflow/workflow-match";
        record.Correlation = new AuditCorrelation
        {
            TraceId = "trace-match",
            CorrelationId = "correlation-match",
            CausationId = "causation-match",
            RequestId = "request-match",
            CommandId = "command-match",
            CallId = "call-match",
            SessionId = "session-match",
            WorkflowRunId = "workflow-run-match",
            ApprovalId = "approval-match",
        };
        record.CommittedFactRef = new AuditCommittedFactReference
        {
            CommittedEventId = "event-match",
            ActorId = "committed-actor-match",
            ActorType = "CommittedActorTypeMatch",
            EventTypeUrl = "type.googleapis.com/aevatar.audit.MatchEvent",
            StateVersion = 42,
        };
        record.Provenance = new AuditExecutionProvenance
        {
            ScopeId = "scope-match",
            RunId = "workflow-run-match",
            CorrelationId = "correlation-match",
            CausationId = "causation-match",
            ActorId = "committed-actor-match",
            ActorStateVersion = 42,
            ActorEventId = "event-match",
        };
        return record;
    }

    private static AuditTrailQuery BuildSingleFilterQuery(string filterName, AuditRecord decoyRecord)
    {
        return filterName switch
        {
            "occurred_from" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-01-02T03:04:04Z")),
                new AuditTrailQuery { OccurredFrom = DateTimeOffset.Parse("2026-01-02T03:04:05Z"), Take = 10 }),
            "occurred_to" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-01-02T03:04:06Z")),
                new AuditTrailQuery { OccurredTo = DateTimeOffset.Parse("2026-01-02T03:04:05Z"), Take = 10 }),
            "scope_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record =>
                {
                    record.ScopeId = "scope-decoy";
                    record.Provenance.ScopeId = "scope-decoy";
                },
                new AuditTrailQuery { ScopeId = "scope-match", Take = 10 }),
            "audit_actor_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.AuditActorId = "actor-decoy",
                new AuditTrailQuery { AuditActorId = "actor-match", Take = 10 }),
            "actor_kind" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.ActorKind = AuditActorKind.Service,
                new AuditTrailQuery { ActorKind = AuditActorKind.NyxidUser, Take = 10 }),
            "identity_key_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.IdentityKeyId = "key-decoy",
                new AuditTrailQuery { IdentityKeyId = "key-match", Take = 10 }),
            "operation_name" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.OperationName = "tool.decoy",
                new AuditTrailQuery { OperationName = "tool.match", Take = 10 }),
            "operation_kind" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.OperationKind = AuditOperationKind.Api,
                new AuditTrailQuery { OperationKind = AuditOperationKind.Tool, Take = 10 }),
            "outcome" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.Outcome = AuditOutcome.Success,
                new AuditTrailQuery { Outcome = AuditOutcome.Denied, Take = 10 }),
            "lifecycle_phase" => ChangeDecoyAndQuery(
                decoyRecord,
                static record =>
                {
                    record.Outcome = AuditOutcome.Accepted;
                    record.LifecyclePhase = AuditLifecyclePhase.Accepted;
                    record.TerminalOutcome = AuditTerminalOutcome.Unspecified;
                    record.Failure = null;
                },
                new AuditTrailQuery { LifecyclePhase = AuditLifecyclePhase.Terminal, Take = 10 }),
            "terminal_outcome" => ChangeDecoyAndQuery(
                decoyRecord,
                static record =>
                {
                    record.Outcome = AuditOutcome.Cancelled;
                    record.TerminalOutcome = AuditTerminalOutcome.Cancelled;
                    record.Failure = null;
                },
                new AuditTrailQuery { TerminalOutcome = AuditTerminalOutcome.Failed, Take = 10 }),
            "sensitivity_level" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.SensitivityLevel = AuditSensitivityLevel.Internal,
                new AuditTrailQuery { SensitivityLevel = AuditSensitivityLevel.Confidential, Take = 10 }),
            "capture_plane" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.CapturePlane = AuditCapturePlane.BoundaryEndpoint,
                new AuditTrailQuery { CapturePlane = AuditCapturePlane.ToolExecution, Take = 10 }),
            "target_kind" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.Target.Kind = "service",
                new AuditTrailQuery { TargetKind = "workflow", Take = 10 }),
            "target_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.Target.Id = "workflow-decoy",
                new AuditTrailQuery { TargetId = "workflow-match", Take = 10 }),
            "trace_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.Correlation.TraceId = "trace-decoy",
                new AuditTrailQuery { TraceId = "trace-match", Take = 10 }),
            "correlation_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record =>
                {
                    record.Correlation.CorrelationId = "correlation-decoy";
                    record.Provenance.CorrelationId = "correlation-decoy";
                },
                new AuditTrailQuery { CorrelationId = "correlation-match", Take = 10 }),
            "causation_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record =>
                {
                    record.Correlation.CausationId = "causation-decoy";
                    record.Provenance.CausationId = "causation-decoy";
                },
                new AuditTrailQuery { CausationId = "causation-match", Take = 10 }),
            "request_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.Correlation.RequestId = "request-decoy",
                new AuditTrailQuery { RequestId = "request-match", Take = 10 }),
            "command_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.Correlation.CommandId = "command-decoy",
                new AuditTrailQuery { CommandId = "command-match", Take = 10 }),
            "call_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.Correlation.CallId = "call-decoy",
                new AuditTrailQuery { CallId = "call-match", Take = 10 }),
            "session_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.Correlation.SessionId = "session-decoy",
                new AuditTrailQuery { SessionId = "session-match", Take = 10 }),
            "workflow_run_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record =>
                {
                    record.Correlation.WorkflowRunId = "workflow-run-decoy";
                    record.Provenance.RunId = "workflow-run-decoy";
                },
                new AuditTrailQuery { WorkflowRunId = "workflow-run-match", Take = 10 }),
            "approval_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.Correlation.ApprovalId = "approval-decoy",
                new AuditTrailQuery { ApprovalId = "approval-match", Take = 10 }),
            "committed_event_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record =>
                {
                    record.CommittedFactRef.CommittedEventId = "event-decoy";
                    record.Provenance.ActorEventId = "event-decoy";
                },
                new AuditTrailQuery { CommittedEventId = "event-match", Take = 10 }),
            "committed_actor_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record =>
                {
                    record.CommittedFactRef.ActorId = "committed-actor-decoy";
                    record.Provenance.ActorId = "committed-actor-decoy";
                },
                new AuditTrailQuery { CommittedActorId = "committed-actor-match", Take = 10 }),
            "committed_actor_type" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.CommittedFactRef.ActorType = "CommittedActorTypeDecoy",
                new AuditTrailQuery { CommittedActorType = "CommittedActorTypeMatch", Take = 10 }),
            "committed_event_type_url" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.CommittedFactRef.EventTypeUrl = "type.googleapis.com/aevatar.audit.DecoyEvent",
                new AuditTrailQuery { CommittedEventTypeUrl = "type.googleapis.com/aevatar.audit.MatchEvent", Take = 10 }),
            "committed_state_version" => ChangeDecoyAndQuery(
                decoyRecord,
                static record =>
                {
                    record.CommittedFactRef.StateVersion = 43;
                    record.Provenance.ActorStateVersion = 43;
                },
                new AuditTrailQuery { CommittedStateVersion = 42, Take = 10 }),
            _ => throw new ArgumentOutOfRangeException(nameof(filterName), filterName, "Unknown audit query filter.")
        };
    }

    private static AuditTrailQuery ChangeDecoyAndQuery(
        AuditRecord decoyRecord,
        Action<AuditRecord> changeDecoy,
        AuditTrailQuery query)
    {
        changeDecoy(decoyRecord);
        return query;
    }
}
