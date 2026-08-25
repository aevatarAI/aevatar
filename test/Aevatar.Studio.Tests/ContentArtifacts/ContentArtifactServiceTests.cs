using System.Security.Cryptography;
using Aevatar.ContentArtifacts.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgents.ContentArtifacts;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests.ContentArtifacts;

public sealed class ContentArtifactServiceTests
{
    [Fact]
    public async Task CreateAsync_ShouldValidateActiveTeamAndNormalizeExecutionProvenance()
    {
        var commandPort = new RecordingCommandPort();
        var service = CreateService(commandPort: commandPort);
        var request = CreateRequest();

        var receipt = await service.CreateAsync(
            " scope-1 ",
            request,
            Principal("owner-1"));

        receipt.ArtifactId.Should().Be("artifact-1");
        commandPort.ScopeId.Should().Be("scope-1");
        commandPort.CreateRequest!.TeamId.Should().Be("team-1");
        commandPort.CreateRequest.FirstRevision.Provenance.ScopeId.Should().Be("scope-1");
        commandPort.CreateRequest.FirstRevision.Provenance.TeamId.Should().Be("team-1");
        commandPort.CreateRequest.FirstRevision.ContentHash.Should().Be(ContentHash("report"));
        commandPort.CreateRequest.Labels.Should().Contain("period", "2026-08-25");
    }

    [Fact]
    public async Task CreateAsync_ShouldFailClosedWhenTeamIsOutsideScope()
    {
        var teams = new RecordingTeamQueryPort(team: new StudioTeamSummaryResponse(
            "team-1",
            "other-scope",
            "Team",
            string.Empty,
            TeamLifecycleStageNames.Active,
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
        var commandPort = new RecordingCommandPort();
        var service = CreateService(teams, commandPort);

        var act = () => service.CreateAsync("scope-1", CreateRequest(), Principal("owner-1"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Team was not found in the requested Scope*");
        commandPort.CreateRequest.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ShouldAllowScopeOwnedArtifactWithoutTeam()
    {
        var teams = new RecordingTeamQueryPort();
        var commandPort = new RecordingCommandPort();
        var service = CreateService(teams, commandPort);

        await service.CreateAsync(
            "scope-1",
            CreateRequest(teamId: null),
            Principal("owner-1"));

        teams.GetCallCount.Should().Be(0);
        commandPort.CreateRequest.Should().NotBeNull();
        commandPort.CreateRequest!.TeamId.Should().BeNull();
        commandPort.CreateRequest.FirstRevision.Provenance.TeamId.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectInvalidLabels()
    {
        var invalidLabels = new IReadOnlyDictionary<string, string>[]
        {
            Enumerable.Range(0, ContentArtifactConventions.MaxLabelCount + 1)
                .ToDictionary(index => $"key-{index}", _ => "value"),
            new Dictionary<string, string> { ["Uppercase"] = "value" },
            new Dictionary<string, string> { ["aevatar.period"] = "value" },
            new Dictionary<string, string> { ["period"] = "line one\nline two" },
            new Dictionary<string, string>
            {
                ["period"] = new string('x', ContentArtifactConventions.MaxLabelValueCharacters + 1),
            },
        };

        foreach (var labels in invalidLabels)
        {
            var service = CreateService(commandPort: new RecordingCommandPort());
            var act = () => service.CreateAsync(
                "scope-1",
                CreateRequest() with { Labels = labels },
                Principal("owner-1"));
            await act.Should().ThrowAsync<ArgumentException>();
        }
    }

    [Theory]
    [InlineData("period", null)]
    [InlineData(null, "2026-08-25")]
    public async Task ListAsync_ShouldRejectHalfSpecifiedLabelFilter(string? labelKey, string? labelValue)
    {
        var service = CreateService();

        var act = () => service.ListAsync(
            "scope-1",
            new ContentArtifactQueryRequest(LabelKey: labelKey, LabelValue: labelValue),
            Principal("owner-1"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*labelKey and labelValue*provided together*");
    }

    [Fact]
    public async Task ListAsync_ShouldNormalizePairedLabelFilter()
    {
        var queryPort = new RecordingQueryPort(BuildCurrentState());
        var service = CreateService(queryPort: queryPort);

        await service.ListAsync(
            "scope-1",
            new ContentArtifactQueryRequest(LabelKey: " period ", LabelValue: " 2026-08-25 "),
            Principal("owner-1"));

        queryPort.LastListQuery!.LabelKey.Should().Be("period");
        queryPort.LastListQuery.LabelValue.Should().Be("2026-08-25");
    }

    [Fact]
    public async Task CreateAsync_ShouldExposeOnlyDedupKeyOccupancyForAnotherOwner()
    {
        var commandPort = new RecordingCommandPort();
        var service = CreateService(
            commandPort: commandPort,
            queryPort: new RecordingQueryPort(BuildCurrentState()));

        var occupied = () => service.CreateAsync(
            "scope-1",
            CreateRequest(),
            Principal("other-owner"));

        var conflict = (await occupied.Should().ThrowAsync<ContentArtifactIdentityConflictException>())
            .Which;
        conflict.Message.Should().Contain("report-dedup")
            .And.NotContain("artifact-1")
            .And.NotContain("owner-1");
        commandPort.CreateRequest.Should().BeNull();

        await service.CreateAsync("scope-1", CreateRequest(), Principal("owner-1", "service"));
        commandPort.CreateRequest.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAsync_ShouldAuthorizeOwnerAndExplicitReaderOnly()
    {
        var queryPort = new RecordingQueryPort(BuildCurrentState());
        var service = CreateService(queryPort: queryPort);

        var owner = await service.GetAsync("scope-1", "artifact-1", Principal("owner-1"));
        var reader = await service.GetAsync("scope-1", "artifact-1", Principal("reader-1"));
        var denied = () => service.GetAsync("scope-1", "artifact-1", Principal("other-user"));

        owner.ArtifactId.Should().Be("artifact-1");
        reader.ArtifactId.Should().Be("artifact-1");
        await denied.Should().ThrowAsync<ContentArtifactNotFoundException>();
    }

    [Fact]
    public async Task GetAsync_ShouldIdentifyOwnerByPrincipalIdOnly()
    {
        var service = CreateService(queryPort: new RecordingQueryPort(BuildCurrentState()));

        var result = await service.GetAsync(
            "scope-1",
            "artifact-1",
            Principal("owner-1", "service"));

        result.ArtifactId.Should().Be("artifact-1");
    }

    [Fact]
    public async Task AppendRevisionAsync_ShouldAuthorizeWriterWithoutDerivingWriteFacts()
    {
        var queryPort = new RecordingQueryPort(BuildCurrentState());
        var commandPort = new RecordingCommandPort();
        var service = CreateService(commandPort: commandPort, queryPort: queryPort);
        var request = new AppendContentArtifactRevisionRequest(
            RevisionWrite("revision two", "revision-2-dedup", parentRevisionId: "revision-1"));

        await service.AppendRevisionAsync(
            "scope-1",
            "artifact-1",
            request,
            Principal("writer-1"));

        commandPort.AppendRequest.Should().NotBeNull();
        commandPort.AppendRequest!.Revision.ParentRevisionId.Should().Be("revision-1");
    }

    [Fact]
    public async Task AppendRevisionAsync_ShouldDispatchWhenAdvisoryReadModelIsMissing()
    {
        var commandPort = new RecordingCommandPort();
        var service = CreateService(
            commandPort: commandPort,
            queryPort: new RecordingQueryPort(current: null));

        await service.AppendRevisionAsync(
            " scope-1 ",
            " artifact-1 ",
            new AppendContentArtifactRevisionRequest(
                RevisionWrite("revision two", "revision-2-dedup", "revision-1")),
            Principal("writer-1"));

        commandPort.ScopeId.Should().Be("scope-1");
        commandPort.AppendArtifactId.Should().Be("artifact-1");
        commandPort.AppendRequest!.Revision.Provenance.ScopeId.Should().Be("scope-1");
        commandPort.AppendRequest.Revision.Provenance.TeamId.Should().Be("caller-supplied-team");
    }

    [Theory]
    [InlineData("advance")]
    [InlineData("redact")]
    [InlineData("expire")]
    public async Task ReadRequiringMutation_ShouldRejectWriterOnlyAndAllowReaderWriterAndOwner(string operation)
    {
        var current = BuildCurrentState() with
        {
            ReaderPrincipalIds = ["reader-1", "editor-1"],
            WriterPrincipalIds = ["writer-1", "editor-1"],
        };
        var commandPort = new RecordingCommandPort();
        var service = CreateService(commandPort: commandPort, queryPort: new RecordingQueryPort(current));

        var writerOnly = () => InvokeMutationAsync(service, operation, Principal("writer-1"));

        await writerOnly.Should().ThrowAsync<ContentArtifactNotFoundException>();
        await InvokeMutationAsync(service, operation, Principal("editor-1"));
        await InvokeMutationAsync(service, operation, Principal("owner-1"));
        commandPort.MutationCallCount.Should().Be(2);
    }

    [Fact]
    public async Task TombstoneAsync_ShouldRemainOwnerOnly()
    {
        var current = BuildCurrentState() with
        {
            ReaderPrincipalIds = ["editor-1"],
            WriterPrincipalIds = ["editor-1"],
        };
        var commandPort = new RecordingCommandPort();
        var service = CreateService(commandPort: commandPort, queryPort: new RecordingQueryPort(current));

        var editor = () => service.TombstoneAsync(
            "scope-1",
            "artifact-1",
            new TombstoneContentArtifactRequest(1, "retention complete"),
            Principal("editor-1"));

        await editor.Should().ThrowAsync<ContentArtifactNotFoundException>();
        await service.TombstoneAsync(
            "scope-1",
            "artifact-1",
            new TombstoneContentArtifactRequest(1, "retention complete"),
            Principal("owner-1"));
        commandPort.MutationCallCount.Should().Be(1);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("get-revision")]
    [InlineData("get-current-revision")]
    [InlineData("get-content")]
    [InlineData("append")]
    [InlineData("advance")]
    [InlineData("redact")]
    [InlineData("expire")]
    [InlineData("tombstone")]
    [InlineData("attach")]
    public async Task ArtifactAclDenial_ShouldBeIndistinguishableFromAbsence(string operation)
    {
        var service = CreateService(queryPort: new RecordingQueryPort(BuildCurrentState()));
        var principal = Principal(operation == "append" ? "unrelated-1" : "writer-1");

        var denied = () => InvokeOperationAsync(service, operation, principal);

        var notFound = (await denied.Should().ThrowAsync<ContentArtifactNotFoundException>()).Which;
        notFound.Message.Should().NotContain("authorized")
            .And.NotContain("tombstoned")
            .And.NotContain("concurrency");
    }

    [Fact]
    public async Task GetRevisionAsync_ShouldReturnNotFoundForMissingRevision()
    {
        var service = CreateService(queryPort: new RecordingQueryPort(BuildCurrentState()));

        var missing = () => service.GetRevisionAsync(
            "scope-1",
            "artifact-1",
            "revision-missing",
            Principal("owner-1"));

        await missing.Should().ThrowAsync<ContentArtifactNotFoundException>();
    }

    [Fact]
    public async Task GetCurrentRevisionAsync_ShouldReturnNotFoundWhenActiveArtifactHasNoRevision()
    {
        var current = BuildCurrentState() with { CurrentRevisionId = null, Revisions = [] };
        var service = CreateService(queryPort: new RecordingQueryPort(current));

        var missing = () => service.GetCurrentRevisionAsync(
            "scope-1",
            "artifact-1",
            Principal("owner-1"));

        await missing.Should().ThrowAsync<ContentArtifactNotFoundException>();
    }

    [Fact]
    public async Task GetRevisionContentAsync_ShouldRejectRedactedRevisionBeforeReadingBackingStore()
    {
        var current = BuildCurrentState() with
        {
            Revisions =
            [
                BuildRevisionResponse() with
                {
                    Availability = ContentArtifactRevisionAvailabilityNames.Redacted,
                    RedactionReason = "privacy",
                },
            ],
        };
        var queryPort = new RecordingQueryPort(current);
        var service = CreateService(queryPort: queryPort);

        var act = () => service.GetRevisionContentAsync(
            "scope-1",
            "artifact-1",
            "revision-1",
            Principal("owner-1"));

        await act.Should().ThrowAsync<ContentArtifactContentUnavailableException>()
            .WithMessage("*redacted*");
        queryPort.ContentReadCount.Should().Be(0);
    }

    [Fact]
    public async Task AttachToRunAsync_ShouldValidateRunAndExactArtifactRevisions()
    {
        var current = BuildCurrentState();
        var queryPort = new RecordingQueryPort(current);
        var runQuery = new RecordingServiceRunQueryPort(BuildRun());
        var runCommands = new RecordingServiceRunResultArtifactAttachmentPort();
        var service = CreateService(
            queryPort: queryPort,
            serviceRunQueryPort: runQuery,
            serviceRunResultArtifactAttachmentPort: runCommands);
        var revision = current.Revisions[0];
        var request = new AttachContentArtifactsToRunRequest(
            "service-1",
            "run-1",
            ExpectedRunStateVersion: 4,
            Artifacts:
            [
                new ContentArtifactReferenceContract(
                    current.ArtifactId,
                    revision.RevisionId,
                    revision.ContentHash,
                    revision.MediaType),
            ]);

        var receipt = await service.AttachToRunAsync(
            "scope-1",
            request,
            Principal("owner-1"));

        receipt.Stage.Should().Be(ContentArtifactCommandStageNames.DispatchAccepted);
        runCommands.Attached.Should().ContainSingle();
        runCommands.Attached[0].ArtifactId.Should().Be("artifact-1");
        runCommands.RunActorId.Should().Be("service-run-actor-1");
        runCommands.ExpectedStateVersion.Should().Be(4);
    }

    private static ContentArtifactService CreateService(
        IStudioTeamQueryPort? teamQueryPort = null,
        RecordingCommandPort? commandPort = null,
        RecordingQueryPort? queryPort = null,
        IServiceRunQueryPort? serviceRunQueryPort = null,
        IServiceRunResultArtifactAttachmentPort? serviceRunResultArtifactAttachmentPort = null) =>
        new(
            teamQueryPort ?? new RecordingTeamQueryPort(),
            commandPort ?? new RecordingCommandPort(),
            queryPort ?? new RecordingQueryPort(BuildCurrentState()),
            serviceRunQueryPort ?? new RecordingServiceRunQueryPort(BuildRun()),
            serviceRunResultArtifactAttachmentPort ?? new RecordingServiceRunResultArtifactAttachmentPort());

    private static CreateContentArtifactRequest CreateRequest(string? teamId = " team-1 ") =>
        new(
            TeamId: teamId,
            Kind: "markdown",
            Title: " Quarterly report ",
            Classification: "internal",
            DedupKey: "report-dedup",
            FirstRevision: RevisionWrite("report", "revision-1-dedup"),
            AccessPolicy: new([" reader-1 "], [" writer-1 "]),
            RetentionPolicy: new("retain-365-days"),
            WorkOrderId: "work-order-1",
            Labels: new Dictionary<string, string> { ["period"] = "2026-08-25" });

    private static ContentArtifactRevisionWriteRequest RevisionWrite(
        string content,
        string dedupKey,
        string? parentRevisionId = null) =>
        new(
            DedupKey: dedupKey,
            MediaType: "text/markdown",
            ContentHash: ContentHash(content),
            ByteLength: System.Text.Encoding.UTF8.GetByteCount(content),
            Provenance: new("caller-supplied-scope", TeamId: "caller-supplied-team", PublishedServiceId: "service-1", RunId: "run-1"),
            InlineContent: System.Text.Encoding.UTF8.GetBytes(content),
            ParentRevisionId: parentRevisionId);

    private static ContentArtifactCurrentStateResponse BuildCurrentState() =>
        new(
            ArtifactId: "artifact-1",
            ScopeId: "scope-1",
            TeamId: "team-1",
            Kind: "markdown",
            Title: "Quarterly report",
            Classification: "internal",
            LifecycleStatus: ContentArtifactLifecycleStatusNames.Active,
            CurrentRevisionId: "revision-1",
            ConcurrencyVersion: 1,
            StateVersion: 1,
            Owner: Principal("owner-1"),
            ReaderPrincipalIds: ["reader-1"],
            WriterPrincipalIds: ["writer-1"],
            RetentionPolicy: new("retain-365-days"),
            WorkOrderId: "work-order-1",
            Revisions: [BuildRevisionResponse()],
            CreatedAtUtc: DateTimeOffset.Parse("2026-07-20T00:00:00Z"),
            UpdatedAtUtc: DateTimeOffset.Parse("2026-07-20T00:00:00Z"));

    private static ContentArtifactRevisionResponse BuildRevisionResponse() =>
        new(
            RevisionId: "revision-1",
            RevisionNumber: 1,
            ParentRevisionId: null,
            MediaType: "text/markdown",
            ByteLength: 6,
            ContentHash: ContentHash("report"),
            Availability: ContentArtifactRevisionAvailabilityNames.Available,
            HasInlineContent: true,
            HasBackingContent: false,
            Provenance: new("scope-1", TeamId: "team-1", PublishedServiceId: "service-1", RunId: "run-1"),
            Citations: [],
            CreatedAtUtc: DateTimeOffset.Parse("2026-07-20T00:00:00Z"));

    private static ServiceRunSnapshot BuildRun() =>
        new(
            ScopeId: "scope-1",
            ServiceId: "service-1",
            ServiceKey: "scope-1:service-1",
            RunId: "run-1",
            CommandId: "command-1",
            CorrelationId: "correlation-1",
            EndpointId: "run",
            ScheduleId: string.Empty,
            ImplementationKind: ServiceImplementationKind.Workflow,
            TargetActorId: "workflow-run-1",
            RevisionId: "service-revision-1",
            DeploymentId: "deployment-1",
            Status: ServiceRunStatus.Completed,
            ActorId: "service-run-actor-1",
            TenantId: "scope-1",
            AppId: "app-1",
            Namespace: "default",
            StateVersion: 4,
            LastEventId: "event-4",
            CreatedAt: DateTimeOffset.Parse("2026-07-20T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-20T00:01:00Z"),
            LastOutput: "done",
            LastError: string.Empty);

    private static ContentArtifactPrincipalContract Principal(string id, string kind = "user") => new(id, kind);

    private static Task<ContentArtifactAcceptedReceipt> InvokeMutationAsync(
        ContentArtifactService service,
        string operation,
        ContentArtifactPrincipalContract principal) =>
        operation switch
        {
            "advance" => service.AdvanceCurrentRevisionAsync(
                "scope-1",
                "artifact-1",
                new AdvanceContentArtifactCurrentRevisionRequest(1, "revision-1"),
                principal),
            "redact" => service.RedactRevisionAsync(
                "scope-1",
                "artifact-1",
                "revision-1",
                new RedactContentArtifactRevisionRequest(1, "privacy request"),
                principal),
            "expire" => service.ExpireRevisionAsync(
                "scope-1",
                "artifact-1",
                "revision-1",
                new ExpireContentArtifactRevisionRequest(1),
                principal),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

    private static async Task InvokeOperationAsync(
        ContentArtifactService service,
        string operation,
        ContentArtifactPrincipalContract principal)
    {
        switch (operation)
        {
            case "get":
                await service.GetAsync("scope-1", "artifact-1", principal);
                break;
            case "get-revision":
                await service.GetRevisionAsync("scope-1", "artifact-1", "revision-1", principal);
                break;
            case "get-current-revision":
                await service.GetCurrentRevisionAsync("scope-1", "artifact-1", principal);
                break;
            case "get-content":
                await service.GetRevisionContentAsync("scope-1", "artifact-1", "revision-1", principal);
                break;
            case "append":
                await service.AppendRevisionAsync(
                    "scope-1",
                    "artifact-1",
                    new AppendContentArtifactRevisionRequest(
                        RevisionWrite("revision two", "revision-2-dedup", "revision-1")),
                    principal);
                break;
            case "advance":
            case "redact":
            case "expire":
                await InvokeMutationAsync(service, operation, principal);
                break;
            case "tombstone":
                await service.TombstoneAsync(
                    "scope-1",
                    "artifact-1",
                    new TombstoneContentArtifactRequest(1, "retention complete"),
                    principal);
                break;
            case "attach":
                var revision = BuildCurrentState().Revisions[0];
                await service.AttachToRunAsync(
                    "scope-1",
                    new AttachContentArtifactsToRunRequest(
                        "service-1",
                        "run-1",
                        4,
                        [new ContentArtifactReferenceContract(
                            "artifact-1",
                            revision.RevisionId,
                            revision.ContentHash,
                            revision.MediaType)]),
                    principal);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    private static string ContentHash(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    private sealed class RecordingTeamQueryPort(StudioTeamSummaryResponse? team = null) : IStudioTeamQueryPort
    {
        private readonly StudioTeamSummaryResponse _team = team ?? new(
            "team-1",
            "scope-1",
            "Team",
            string.Empty,
            TeamLifecycleStageNames.Active,
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        public int GetCallCount { get; private set; }

        public Task<StudioTeamRosterResponse> ListAsync(string scopeId, StudioTeamRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, [_team]));

        public Task<StudioTeamSummaryResponse?> GetAsync(string scopeId, string teamId, CancellationToken ct = default)
        {
            GetCallCount++;
            return Task.FromResult<StudioTeamSummaryResponse?>(_team);
        }
    }

    private sealed class RecordingCommandPort : IContentArtifactCommandPort
    {
        public string? ScopeId { get; private set; }
        public CreateContentArtifactRequest? CreateRequest { get; private set; }
        public AppendContentArtifactRevisionRequest? AppendRequest { get; private set; }
        public string? AppendArtifactId { get; private set; }
        public int MutationCallCount { get; private set; }

        public Task<ContentArtifactAcceptedReceipt> CreateAsync(string scopeId, CreateContentArtifactRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default)
        {
            ScopeId = scopeId;
            CreateRequest = request;
            return Receipt();
        }

        public Task<ContentArtifactAcceptedReceipt> AppendRevisionAsync(string scopeId, string artifactId, AppendContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default)
        {
            ScopeId = scopeId;
            AppendArtifactId = artifactId;
            AppendRequest = request;
            return Receipt();
        }

        public Task<ContentArtifactAcceptedReceipt> AdvanceCurrentRevisionAsync(string scopeId, string artifactId, AdvanceContentArtifactCurrentRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => MutationReceipt();
        public Task<ContentArtifactAcceptedReceipt> RedactRevisionAsync(string scopeId, string artifactId, string revisionId, RedactContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => MutationReceipt();
        public Task<ContentArtifactAcceptedReceipt> ExpireRevisionAsync(string scopeId, string artifactId, string revisionId, ExpireContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => MutationReceipt();
        public Task<ContentArtifactAcceptedReceipt> TombstoneAsync(string scopeId, string artifactId, TombstoneContentArtifactRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => MutationReceipt();

        private Task<ContentArtifactAcceptedReceipt> MutationReceipt()
        {
            MutationCallCount++;
            return Receipt();
        }

        private static Task<ContentArtifactAcceptedReceipt> Receipt() =>
            Task.FromResult(new ContentArtifactAcceptedReceipt("artifact-1", "command-1", "correlation-1", ContentArtifactCommandStageNames.DispatchAccepted));
    }

    private sealed class RecordingQueryPort(ContentArtifactCurrentStateResponse? current) : IContentArtifactQueryPort
    {
        public int ContentReadCount { get; private set; }
        public ContentArtifactQueryRequest? LastListQuery { get; private set; }

        public Task<ContentArtifactListResponse> ListAsync(string scopeId, string ownerPrincipalId, ContentArtifactQueryRequest query, CancellationToken ct = default)
        {
            LastListQuery = query;
            return Task.FromResult(new ContentArtifactListResponse(
                scopeId,
                current == null ? [] : [current]));
        }

        public Task<ContentArtifactCurrentStateResponse?> GetAsync(string scopeId, string artifactId, CancellationToken ct = default) =>
            Task.FromResult<ContentArtifactCurrentStateResponse?>(current);

        public Task<ContentArtifactCurrentStateResponse?> GetByDedupKeyAsync(string scopeId, string dedupKey, CancellationToken ct = default) =>
            Task.FromResult<ContentArtifactCurrentStateResponse?>(current);

        public Task<ContentArtifactRevisionContentResponse> GetRevisionContentAsync(string scopeId, string artifactId, string revisionId, ContentArtifactPrincipalContract requester, CancellationToken ct = default)
        {
            ContentReadCount++;
            var revision = current!.Revisions.Single(item => item.RevisionId == revisionId);
            return Task.FromResult(new ContentArtifactRevisionContentResponse(
                new ContentArtifactReferenceContract(artifactId, revisionId, revision.ContentHash, revision.MediaType),
                System.Text.Encoding.UTF8.GetBytes("report")));
        }
    }

    private sealed class RecordingServiceRunQueryPort(ServiceRunSnapshot run) : IServiceRunQueryPort
    {
        public Task<IReadOnlyList<ServiceRunSnapshot>> ListAsync(ServiceRunQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceRunSnapshot>>([run]);

        public Task<ServiceRunSnapshot?> GetByRunIdAsync(string scopeId, string serviceId, string runId, CancellationToken ct = default) =>
            Task.FromResult<ServiceRunSnapshot?>(run);

        public Task<ServiceRunSnapshot?> GetByCommandIdAsync(string scopeId, string serviceId, string commandId, CancellationToken ct = default) =>
            Task.FromResult<ServiceRunSnapshot?>(run);
    }

    private sealed class RecordingServiceRunResultArtifactAttachmentPort : IServiceRunResultArtifactAttachmentPort
    {
        public string? RunActorId { get; private set; }
        public long ExpectedStateVersion { get; private set; }
        public List<ContentArtifactReference> Attached { get; } = [];

        public Task<ServiceRunArtifactAttachmentResult> AttachResultArtifactsAsync(
            string runActorId,
            string runId,
            long expectedStateVersion,
            IReadOnlyList<ContentArtifactReference> resultArtifacts,
            CancellationToken ct = default)
        {
            RunActorId = runActorId;
            ExpectedStateVersion = expectedStateVersion;
            Attached.AddRange(resultArtifacts.Select(static artifact => artifact.Clone()));
            return Task.FromResult(new ServiceRunArtifactAttachmentResult(runId, "attach-command-1", "attach-correlation-1"));
        }
    }
}
