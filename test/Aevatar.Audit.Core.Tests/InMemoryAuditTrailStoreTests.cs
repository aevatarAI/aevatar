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
            TargetKind = "workflow",
            TargetId = "wf-audit-2",
            RequestId = "req-audit-2",
            WorkflowRunId = "run-audit-2",
            Take = 10
        });

        page.Records.Select(static record => record.AuditId).ShouldBe(["audit-2"]);
        page.NextCursor.ShouldBeNull();
        page.Watermark.ShouldNotBeNull();
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
            RequestSummary = "request summary",
            ResultSummary = "result summary"
        };
    }
}
