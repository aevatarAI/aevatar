using System.Security.Cryptography;
using System.Text;
using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.GAgents.ContentArtifacts;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application.Delivery;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowAcceptanceArtifactMaterializerTests
{
    private const string RunId = "run-alpha";
    private const string ScheduleId = "schedule-alpha";

    [Fact]
    public async Task MaterializeAsync_WhenCompletedRunHasNoArtifact_ShouldCreateDeterministicArtifact()
    {
        var context = new TestContext();
        var delivery = Delivery();
        var output = AcceptanceOutput(delivery);
        context.Runs.Items = [Run(delivery, ServiceRunStatus.Completed, output)];

        var result = await context.Materializer.MaterializeAsync(delivery);

        result.Status.Should().Be(WorkflowAcceptanceArtifactMaterializationStatus.Pending);
        result.Code.Should().Be("acceptance_artifact_creation_accepted");
        var call = context.Service.Creates.Should().ContainSingle().Subject;
        var identity = WorkflowAcceptanceArtifactContract.BuildIdentity(delivery, RunId);
        var digest = Digest(output);
        call.ScopeId.Should().Be(delivery.Installation!.ScopeId);
        call.Owner.Should().Be(new ContentArtifactPrincipalContract("caller-alpha", "user"));
        call.Request.TeamId.Should().Be(delivery.Installation.TeamId);
        call.Request.Kind.Should().Be(WorkflowAcceptanceArtifactContract.Kind);
        call.Request.Classification.Should().Be(WorkflowAcceptanceArtifactContract.Classification);
        call.Request.DedupKey.Should().Be(identity.DedupKey);
        call.Request.FirstRevision.DedupKey.Should().Be($"{identity.DedupKey}/revision/1");
        call.Request.FirstRevision.MediaType.Should().Be(WorkflowAcceptanceArtifactContract.MediaType);
        call.Request.FirstRevision.ContentHash.Should().Be(digest);
        call.Request.FirstRevision.ByteLength.Should().Be(Encoding.UTF8.GetByteCount(output));
        call.Request.FirstRevision.InlineContent.Should().Equal(Encoding.UTF8.GetBytes(output));
        call.Request.FirstRevision.Provenance.Should().Be(new ContentArtifactExecutionProvenanceContract(
            delivery.Installation.ScopeId,
            delivery.Installation.TeamId,
            delivery.Installation.MemberId,
            delivery.Installation.WorkflowId,
            delivery.Installation.PublishedServiceId,
            RunId));
        context.Service.Attachments.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializeAsync_WhenCommittedArtifactIsVisible_ShouldAttachExactRevisionWithRunCas()
    {
        var context = new TestContext();
        var delivery = Delivery();
        var output = AcceptanceOutput(delivery);
        var run = Run(delivery, ServiceRunStatus.Completed, output);
        context.Runs.Items = [run];
        context.Artifacts.Current = Artifact(delivery, run, output);
        context.Artifacts.Content = Encoding.UTF8.GetBytes(output);

        var result = await context.Materializer.MaterializeAsync(delivery);

        result.Status.Should().Be(WorkflowAcceptanceArtifactMaterializationStatus.Pending);
        result.Code.Should().Be("acceptance_artifact_attachment_accepted");
        context.Service.Creates.Should().BeEmpty();
        var call = context.Service.Attachments.Should().ContainSingle().Subject;
        var identity = WorkflowAcceptanceArtifactContract.BuildIdentity(delivery, RunId);
        call.ScopeId.Should().Be(delivery.Installation!.ScopeId);
        call.Owner.Should().Be(new ContentArtifactPrincipalContract("caller-alpha", "user"));
        call.Request.PublishedServiceId.Should().Be(delivery.Installation.PublishedServiceId);
        call.Request.RunId.Should().Be(RunId);
        call.Request.ExpectedRunStateVersion.Should().Be(run.StateVersion);
        call.Request.Artifacts.Should().ContainSingle().Which.Should().Be(
            new ContentArtifactReferenceContract(
                identity.ArtifactId,
                identity.RevisionId,
                Digest(output),
                WorkflowAcceptanceArtifactContract.MediaType));
    }

    [Fact]
    public async Task MaterializeAsync_WhenExactRevisionIsAlreadyAttached_ShouldBeSatisfiedWithoutWrites()
    {
        var context = new TestContext();
        var delivery = Delivery();
        var output = AcceptanceOutput(delivery);
        var identity = WorkflowAcceptanceArtifactContract.BuildIdentity(delivery, RunId);
        context.Runs.Items =
        [
            Run(delivery, ServiceRunStatus.Completed, output) with
            {
                ResultArtifacts =
                [
                    new ContentArtifactReference
                    {
                        ArtifactId = identity.ArtifactId,
                        RevisionId = identity.RevisionId,
                        ContentHash = Digest(output),
                        MediaType = WorkflowAcceptanceArtifactContract.MediaType,
                    },
                ],
            },
        ];

        var result = await context.Materializer.MaterializeAsync(delivery);

        result.Status.Should().Be(WorkflowAcceptanceArtifactMaterializationStatus.Satisfied);
        result.Code.Should().Be("acceptance_artifact_attached");
        context.Artifacts.DedupQueries.Should().BeEmpty();
        context.Service.Creates.Should().BeEmpty();
        context.Service.Attachments.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializeAsync_WhenCurrentRunIsNotCompleted_ShouldHaveNoSideEffects()
    {
        var context = new TestContext();
        var delivery = Delivery();
        context.Runs.Items = [Run(delivery, ServiceRunStatus.Accepted, string.Empty)];

        var result = await context.Materializer.MaterializeAsync(delivery);

        result.Status.Should().Be(WorkflowAcceptanceArtifactMaterializationStatus.Satisfied);
        result.Code.Should().Be("acceptance_run_not_completed");
        context.Artifacts.DedupQueries.Should().BeEmpty();
        context.Service.Creates.Should().BeEmpty();
        context.Service.Attachments.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializeAsync_WhenNoTriggerRunHasNoDeliveryAttribution_ShouldRemainPending()
    {
        var context = new TestContext();
        var delivery = ManualDelivery();
        context.Runs.Items =
        [
            Run(delivery, ServiceRunStatus.Completed, AcceptanceOutput(delivery), scheduleId: string.Empty) with
            {
                ScheduleOperationId = string.Empty,
            },
        ];

        var result = await context.Materializer.MaterializeAsync(delivery);

        result.Status.Should().Be(WorkflowAcceptanceArtifactMaterializationStatus.Pending);
        result.Code.Should().Be("acceptance_run_pending");
        context.Artifacts.DedupQueries.Should().BeEmpty();
        context.Service.Creates.Should().BeEmpty();
        context.Service.Attachments.Should().BeEmpty();
    }

    public static TheoryData<string, string> InvalidOutputs => new()
    {
        { string.Empty, "acceptance_output_missing" },
        { "{", "acceptance_output_contract_invalid" },
        { "{\"workflow\":\"wrong\",\"mode\":\"preview\",\"side_effects\":false}", "acceptance_output_contract_invalid" },
        { "{\"workflow\":\"fin_budget_variance_monitor\",\"mode\":\"live\",\"side_effects\":false}", "acceptance_output_contract_invalid" },
        { "{\"workflow\":\"fin_budget_variance_monitor\",\"mode\":\"preview\",\"side_effects\":true}", "acceptance_output_contract_invalid" },
        { new string('x', ContentArtifactConventions.MaxInlineContentBytes + 1), "acceptance_output_too_large" },
    };

    [Theory]
    [MemberData(nameof(InvalidOutputs))]
    public async Task MaterializeAsync_WhenCompletedOutputViolatesContract_ShouldReturnTypedTerminalFailure(
        string output,
        string expectedCode)
    {
        var context = new TestContext();
        var delivery = Delivery();
        context.Runs.Items = [Run(delivery, ServiceRunStatus.Completed, output)];

        var result = await context.Materializer.MaterializeAsync(delivery);

        result.Status.Should().Be(WorkflowAcceptanceArtifactMaterializationStatus.TerminalFailure);
        result.Code.Should().Be(expectedCode);
        context.Artifacts.DedupQueries.Should().BeEmpty();
        context.Service.Creates.Should().BeEmpty();
        context.Service.Attachments.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializeAsync_WhenDeterministicIdentityContainsConflictingArtifact_ShouldBeTerminal()
    {
        var context = new TestContext();
        var delivery = Delivery();
        var output = AcceptanceOutput(delivery);
        var run = Run(delivery, ServiceRunStatus.Completed, output);
        context.Runs.Items = [run];
        context.Artifacts.Current = Artifact(delivery, run, output) with { Kind = "binary" };

        var result = await context.Materializer.MaterializeAsync(delivery);

        result.Status.Should().Be(WorkflowAcceptanceArtifactMaterializationStatus.TerminalFailure);
        result.Code.Should().Be("acceptance_artifact_identity_conflict");
        context.Service.Creates.Should().BeEmpty();
        context.Service.Attachments.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializeAsync_WhenScheduledRunBelongsToOldAttempt_ShouldRemainPending()
    {
        var context = new TestContext();
        var delivery = ScheduledDelivery();
        context.Runs.Items =
        [
            Run(delivery, ServiceRunStatus.Completed, AcceptanceOutput(delivery), ScheduleId) with
            {
                ScheduleOperationId = "installation-alpha:provision:a0",
            },
        ];

        var result = await context.Materializer.MaterializeAsync(delivery);

        result.Status.Should().Be(WorkflowAcceptanceArtifactMaterializationStatus.Pending);
        result.Code.Should().Be("acceptance_run_pending");
        context.Service.Creates.Should().BeEmpty();
        context.Service.Attachments.Should().BeEmpty();
    }

    [Fact]
    public async Task MaterializeAsync_WhenRunChangesBeforeAttachment_ShouldRemainPendingForRetry()
    {
        var context = new TestContext();
        var delivery = Delivery();
        var output = AcceptanceOutput(delivery);
        var run = Run(delivery, ServiceRunStatus.Completed, output);
        context.Runs.Items = [run];
        context.Artifacts.Current = Artifact(delivery, run, output);
        context.Artifacts.Content = Encoding.UTF8.GetBytes(output);
        context.Service.AttachmentFailure = new InvalidOperationException("Run CAS changed.");

        var result = await context.Materializer.MaterializeAsync(delivery);

        result.Status.Should().Be(WorkflowAcceptanceArtifactMaterializationStatus.Pending);
        result.Code.Should().Be("acceptance_run_projection_changed");
        context.Service.Attachments.Should().ContainSingle();
    }

    private static WorkflowDeliverySnapshot Delivery() => ScheduledDelivery();

    private static WorkflowDeliverySnapshot ManualDelivery()
    {
        var delivery = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.ProvisioningAccepted);
        return delivery with
        {
            Installation = delivery.Installation! with
            {
                TriggerIntent = new WorkflowDeliveryTriggerIntent(
                    WorkflowDeliveryTriggerKind.None,
                    null,
                    null,
                    RunImmediately: false),
                ScheduleId = null,
                OperationId = "installation-alpha:provision:a1",
            },
        };
    }

    private static WorkflowDeliverySnapshot ScheduledDelivery()
    {
        var delivery = WorkflowDeliveryProvisioningExecutorTests.Delivery(
            WorkflowInstallationStatus.ProvisioningAccepted);
        return delivery with
        {
            Installation = delivery.Installation! with
            {
                TriggerIntent = new WorkflowDeliveryTriggerIntent(
                    WorkflowDeliveryTriggerKind.OneShot,
                    null,
                    "UTC",
                    RunImmediately: true),
                ScheduleId = ScheduleId,
                OperationId = "installation-alpha:provision:a1",
            },
        };
    }

    private static string AcceptanceOutput(WorkflowDeliverySnapshot delivery) =>
        $"{{\"workflow\":\"{delivery.Package.WorkflowName}\",\"mode\":\"preview\",\"side_effects\":false}}";

    private static ServiceRunSnapshot Run(
        WorkflowDeliverySnapshot delivery,
        ServiceRunStatus status,
        string output,
        string scheduleId = ScheduleId)
    {
        var installation = delivery.Installation!;
        return new ServiceRunSnapshot(
            installation.ScopeId,
            installation.PublishedServiceId!,
            "service-key-alpha",
            RunId,
            "command-alpha",
            "correlation-alpha",
            "chat",
            scheduleId,
            ServiceImplementationKind.Workflow,
            "actor-alpha",
            installation.RevisionId!,
            "deployment-alpha",
            status,
            "service-run:run-alpha",
            installation.ScopeId,
            "app-alpha",
            "namespace-alpha",
            StateVersion: 21,
            "event-alpha",
            installation.CreatedAtUtc.AddMinutes(1),
            installation.CreatedAtUtc.AddMinutes(2),
            output,
            string.Empty)
        {
            ScheduleOperationId = scheduleId.Length == 0 ? string.Empty : installation.OperationId,
        };
    }

    private static ContentArtifactCurrentStateResponse Artifact(
        WorkflowDeliverySnapshot delivery,
        ServiceRunSnapshot run,
        string output)
    {
        var installation = delivery.Installation!;
        var identity = WorkflowAcceptanceArtifactContract.BuildIdentity(delivery, run.RunId);
        var digest = Digest(output);
        return new ContentArtifactCurrentStateResponse(
            identity.ArtifactId,
            installation.ScopeId,
            installation.TeamId,
            WorkflowAcceptanceArtifactContract.Kind,
            "Acceptance output",
            WorkflowAcceptanceArtifactContract.Classification,
            ContentArtifactLifecycleStatusNames.Active,
            identity.RevisionId,
            ConcurrencyVersion: 1,
            StateVersion: 41,
            new ContentArtifactPrincipalContract("caller-alpha", "user"),
            [],
            [],
            RetentionPolicy: null,
            WorkOrderId: null,
            [new ContentArtifactRevisionResponse(
                identity.RevisionId,
                RevisionNumber: 1,
                ParentRevisionId: null,
                WorkflowAcceptanceArtifactContract.MediaType,
                Encoding.UTF8.GetByteCount(output),
                digest,
                ContentArtifactRevisionAvailabilityNames.Available,
                HasInlineContent: true,
                HasBackingContent: false,
                new ContentArtifactExecutionProvenanceContract(
                    installation.ScopeId,
                    installation.TeamId,
                    installation.MemberId,
                    installation.WorkflowId,
                    installation.PublishedServiceId,
                    run.RunId),
                [],
                installation.CreatedAtUtc.AddMinutes(2))],
            installation.CreatedAtUtc.AddMinutes(2),
            installation.CreatedAtUtc.AddMinutes(2));
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class TestContext
    {
        public RecordingRunQueryPort Runs { get; } = new();
        public RecordingArtifactQueryPort Artifacts { get; } = new();
        public RecordingArtifactService Service { get; } = new();
        public WorkflowAcceptanceArtifactMaterializer Materializer { get; }

        public TestContext()
        {
            Materializer = new WorkflowAcceptanceArtifactMaterializer(Runs, Artifacts, Service);
        }
    }

    private sealed class RecordingRunQueryPort : IServiceRunQueryPort
    {
        public IReadOnlyList<ServiceRunSnapshot> Items { get; set; } = [];
        public List<ServiceRunQuery> Queries { get; } = [];

        public Task<IReadOnlyList<ServiceRunSnapshot>> ListAsync(
            ServiceRunQuery query,
            CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult(Items);
        }

        public Task<ServiceRunSnapshot?> GetByRunIdAsync(
            string scopeId,
            string serviceId,
            string runId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServiceRunSnapshot?> GetByCommandIdAsync(
            string scopeId,
            string serviceId,
            string commandId,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingArtifactQueryPort : IContentArtifactQueryPort
    {
        public ContentArtifactCurrentStateResponse? Current { get; set; }
        public byte[] Content { get; set; } = [];
        public List<(string ScopeId, string DedupKey)> DedupQueries { get; } = [];

        public Task<ContentArtifactCurrentStateResponse?> GetByDedupKeyAsync(
            string scopeId,
            string dedupKey,
            CancellationToken ct = default)
        {
            DedupQueries.Add((scopeId, dedupKey));
            return Task.FromResult(Current);
        }

        public Task<ContentArtifactRevisionContentResponse> GetRevisionContentAsync(
            string scopeId,
            string artifactId,
            string revisionId,
            ContentArtifactPrincipalContract requester,
            CancellationToken ct = default) =>
            Task.FromResult(new ContentArtifactRevisionContentResponse(
                new ContentArtifactReferenceContract(
                    artifactId,
                    revisionId,
                    Digest(Encoding.UTF8.GetString(Content)),
                    WorkflowAcceptanceArtifactContract.MediaType),
                Content));

        public Task<ContentArtifactListResponse> ListAsync(
            string scopeId,
            string requesterPrincipalId,
            ContentArtifactQueryRequest query,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ContentArtifactCurrentStateResponse?> GetAsync(
            string scopeId,
            string artifactId,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingArtifactService : IContentArtifactService
    {
        public List<CreateCall> Creates { get; } = [];
        public List<AttachmentCall> Attachments { get; } = [];
        public InvalidOperationException? AttachmentFailure { get; set; }

        public Task<ContentArtifactAcceptedReceipt> CreateAsync(
            string scopeId,
            CreateContentArtifactRequest request,
            ContentArtifactPrincipalContract requester,
            CancellationToken ct = default)
        {
            Creates.Add(new CreateCall(scopeId, request, requester));
            return Task.FromResult(new ContentArtifactAcceptedReceipt(
                "artifact-alpha",
                "command-alpha",
                "correlation-alpha",
                ContentArtifactCommandStageNames.DispatchAccepted));
        }

        public Task<ContentArtifactRunAttachmentReceipt> AttachToRunAsync(
            string scopeId,
            AttachContentArtifactsToRunRequest request,
            ContentArtifactPrincipalContract requester,
            CancellationToken ct = default)
        {
            Attachments.Add(new AttachmentCall(scopeId, request, requester));
            if (AttachmentFailure != null)
                throw AttachmentFailure;
            return Task.FromResult(new ContentArtifactRunAttachmentReceipt(
                request.RunId,
                "command-attach",
                "correlation-attach",
                ContentArtifactCommandStageNames.DispatchAccepted));
        }

        public Task<ContentArtifactListResponse> ListAsync(string scopeId, ContentArtifactQueryRequest query, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactCurrentStateResponse> GetAsync(string scopeId, string artifactId, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactRevisionResponse> GetRevisionAsync(string scopeId, string artifactId, string revisionId, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactRevisionResponse> GetCurrentRevisionAsync(string scopeId, string artifactId, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactRevisionContentResponse> GetRevisionContentAsync(string scopeId, string artifactId, string revisionId, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactAcceptedReceipt> AppendRevisionAsync(string scopeId, string artifactId, AppendContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactAcceptedReceipt> AdvanceCurrentRevisionAsync(string scopeId, string artifactId, AdvanceContentArtifactCurrentRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactAcceptedReceipt> RedactRevisionAsync(string scopeId, string artifactId, string revisionId, RedactContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactAcceptedReceipt> ExpireRevisionAsync(string scopeId, string artifactId, string revisionId, ExpireContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactAcceptedReceipt> TombstoneAsync(string scopeId, string artifactId, TombstoneContentArtifactRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed record CreateCall(
        string ScopeId,
        CreateContentArtifactRequest Request,
        ContentArtifactPrincipalContract Owner);

    private sealed record AttachmentCall(
        string ScopeId,
        AttachContentArtifactsToRunRequest Request,
        ContentArtifactPrincipalContract Owner);
}
