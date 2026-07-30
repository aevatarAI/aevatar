using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.GAgents.StatusDashboard;
using Aevatar.Mainnet.Host.Api.Status;
using FluentAssertions;

namespace Aevatar.Capabilities.Tests;

public sealed class AuditQueryIndexStatusProbeExecutorTests
{
    [Fact]
    public async Task ProbeAsync_WhenAuditQuerySucceeds_ShouldReportOk()
    {
        var queryPort = new RecordingQueryPort();
        var executor = new AuditQueryIndexStatusProbeExecutor(queryPort);

        var outcome = await executor.ProbeAsync(Descriptor(), CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        outcome.Detail.Should().Be("audit_query_index_available");
        var query = queryPort.Queries.Should().ContainSingle().Which;
        query.Take.Should().Be(1);
        query.OccurredFrom.Should().NotBeNull();
        query.OccurredTo.Should().BeAfter(query.OccurredFrom!.Value);
    }

    [Fact]
    public async Task ProbeAsync_WhenAuditQueryFails_ShouldReportDownWithoutSensitiveDetail()
    {
        var queryPort = new RecordingQueryPort
        {
            Exception = new InvalidOperationException(
                "https://elastic-secret.example:9200 password=secret raw-backend-detail"),
        };
        var executor = new AuditQueryIndexStatusProbeExecutor(queryPort);

        var outcome = await executor.ProbeAsync(Descriptor(), CancellationToken.None);

        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("audit_query_index_unavailable:InvalidOperationException");
        outcome.ErrorMessage.Should().Be("Audit trail query/index probe failed.");
        (outcome.Detail + outcome.ErrorMessage)
            .Should().NotContain("elastic-secret").And.NotContain("password=secret").And.NotContain("raw-backend-detail");
    }

    private static HealthProbeTargetDescriptor Descriptor() =>
        new()
        {
            Slug = "audit-query-index",
            DisplayName = "Audit Trail Query / Index",
            Category = "feature",
            Severity = "standard",
            ProbeKind = "audit_query_index",
            IntervalSeconds = 60,
            TimeoutMs = 5_000,
            Enabled = true,
        };

    private sealed class RecordingQueryPort : IAuditTrailQueryPort
    {
        public List<AuditTrailQuery> Queries { get; } = [];

        public Exception? Exception { get; init; }

        public Task<AuditTrailPage> QueryAsync(
            AuditTrailQuery query,
            CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            if (Exception is not null)
                return Task.FromException<AuditTrailPage>(Exception);

            return Task.FromResult(new AuditTrailPage(
                [],
                null,
                DateTimeOffset.UtcNow,
                AuditQueryCoverage.Create(
                    query,
                    truncated: false,
                    ingestionWatermark: null,
                    completeThrough: null,
                    schemaCompatibility: AuditSchemaCompatibility.Current)));
        }
    }
}
