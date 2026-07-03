using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Models;
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
        page.Watermark.ShouldNotBeNull();
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
    [InlineData("sensitivity_level")]
    [InlineData("capture_plane")]
    [InlineData("target_kind")]
    [InlineData("target_id")]
    [InlineData("trace_id")]
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
    public async Task QueryAsync_PaginatesInOccurrenceOrder()
    {
        var store = new InMemoryAuditTrailStore();
        await store.AppendManyAsync(
        [
            CreateRecord("audit-3", "scope-a", "actor-a", "api.call", AuditOutcome.Success, seconds: 3),
            CreateRecord("audit-1", "scope-a", "actor-a", "api.call", AuditOutcome.Success, seconds: 1),
            CreateRecord("audit-2", "scope-a", "actor-a", "api.call", AuditOutcome.Success, seconds: 2)
        ]);

        var first = await store.QueryAsync(new AuditTrailQuery { ScopeId = "scope-a", Take = 2 });
        var second = await store.QueryAsync(new AuditTrailQuery { ScopeId = "scope-a", Cursor = first.NextCursor, Take = 2 });

        first.Records.Select(static record => record.AuditId).ShouldBe(["audit-1", "audit-2"]);
        first.NextCursor.ShouldNotBeNull();
        second.Records.Select(static record => record.AuditId).ShouldBe(["audit-3"]);
        second.NextCursor.ShouldBeNull();
    }

    private static AuditRecord CreateRecord(
        string auditId,
        string scopeId,
        string auditActorId,
        string operationName,
        AuditOutcome outcome,
        int seconds = 0)
    {
        return new AuditRecord
        {
            AuditId = auditId,
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-01-02T03:04:05Z").AddSeconds(seconds)),
            ScopeId = scopeId,
            AuditActorId = auditActorId,
            IdentityKeyId = "key-1",
            ActorKind = AuditActorKind.NyxidUser,
            CredentialSource = AuditCredentialSource.NyxidAssertion,
            OperationKind = operationName.StartsWith("tool.", StringComparison.Ordinal) ? AuditOperationKind.Tool : AuditOperationKind.Api,
            OperationName = operationName,
            SensitivityLevel = AuditSensitivityLevel.Confidential,
            Outcome = outcome,
            CapturePlane = operationName.StartsWith("tool.", StringComparison.Ordinal)
                ? AuditCapturePlane.ToolExecution
                : AuditCapturePlane.BoundaryEndpoint,
            Target = new AuditTarget { Kind = "workflow", Id = $"wf-{auditId}" },
            Correlation = new AuditCorrelation
            {
                TraceId = $"trace-{auditId}",
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
            ResultSummary = "result summary"
        };
    }

    private static AuditRecord CreateFocusedRecord(string auditId)
    {
        return new AuditRecord
        {
            AuditId = auditId,
            OccurredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-01-02T03:04:05Z")),
            ScopeId = "scope-match",
            AuditActorId = "actor-match",
            IdentityKeyId = "key-match",
            ActorKind = AuditActorKind.NyxidUser,
            CredentialSource = AuditCredentialSource.NyxidAssertion,
            OperationKind = AuditOperationKind.Tool,
            OperationName = "tool.match",
            SensitivityLevel = AuditSensitivityLevel.Confidential,
            Outcome = AuditOutcome.Denied,
            CapturePlane = AuditCapturePlane.ToolExecution,
            Target = new AuditTarget { Kind = "workflow", Id = "workflow-match" },
            Correlation = new AuditCorrelation
            {
                TraceId = "trace-match",
                RequestId = "request-match",
                CommandId = "command-match",
                CallId = "call-match",
                SessionId = "session-match",
                WorkflowRunId = "workflow-run-match",
                ApprovalId = "approval-match"
            },
            CommittedFactRef = new AuditCommittedFactReference
            {
                CommittedEventId = "event-match",
                ActorId = "committed-actor-match",
                ActorType = "CommittedActorTypeMatch",
                EventTypeUrl = "type.googleapis.com/aevatar.audit.MatchEvent",
                StateVersion = 42
            },
            RequestSummary = "request summary",
            ResultSummary = "result summary"
        };
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
                static record => record.ScopeId = "scope-decoy",
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
                static record => record.Correlation.WorkflowRunId = "workflow-run-decoy",
                new AuditTrailQuery { WorkflowRunId = "workflow-run-match", Take = 10 }),
            "approval_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.Correlation.ApprovalId = "approval-decoy",
                new AuditTrailQuery { ApprovalId = "approval-match", Take = 10 }),
            "committed_event_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.CommittedFactRef.CommittedEventId = "event-decoy",
                new AuditTrailQuery { CommittedEventId = "event-match", Take = 10 }),
            "committed_actor_id" => ChangeDecoyAndQuery(
                decoyRecord,
                static record => record.CommittedFactRef.ActorId = "committed-actor-decoy",
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
                static record => record.CommittedFactRef.StateVersion = 43,
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
