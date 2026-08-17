using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.WorkflowDelivery;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using DeliveryApplication = global::Aevatar.Studio.Application.Studio.Abstractions;
using ProtobufValue = Google.Protobuf.WellKnownTypes.Value;

namespace Aevatar.Studio.Tests;

public sealed class ProjectionWorkflowDeliveryQueryPortTests
{
    [Fact]
    public async Task GetForScopeAsync_ShouldFilterBeforeMappingAndReturnTypedImmutablePackage()
    {
        var reader = new RecordingReader
        {
            QueryResult = Result(ValidDocument()),
        };
        var port = new ProjectionWorkflowDeliveryQueryPort(reader);

        var snapshot = await port.GetForScopeAsync("delivery-alpha", "scope-alpha");

        snapshot.Should().NotBeNull();
        snapshot!.TargetScopeId.Should().Be("scope-alpha");
        snapshot.Package.SourceYaml.Should().Be("name: workflow-alpha\n");
        snapshot.Package.SourceHash.Should().Be("sha256-alpha");
        snapshot.Package.PackageHash.Should().Be("package-hash-alpha");
        snapshot.Package.AcceptancePolicy.Mode.Should().Be(
            DeliveryApplication.WorkflowDeliveryAcceptanceMode.AutomaticPreview);
        snapshot.Package.AcceptancePolicy.InputDeclared.Should().BeTrue();
        snapshot.Package.AcceptancePolicy.Input.Literals.Fields.Should().ContainKey("dry_run")
            .WhoseValue.BoolValue.Should().BeTrue();
        snapshot.Package.AcceptancePolicy.Input.Bindings.Select(static value => value.Key)
            .Should().Equal("created_month", "owner_id");
        snapshot.Package.AcceptancePolicy.Input.Bindings[0].Source.Should().BeEquivalentTo(
            new DeliveryApplication.WorkflowDeliveryInstallationCreatedAtUtcInput(
                DeliveryApplication.WorkflowDeliveryAcceptanceDateProjection.UtcYearMonth,
                -2));
        snapshot.Package.AcceptancePolicy.Input.Bindings[1].Source.Should().BeOfType<
            DeliveryApplication.WorkflowDeliveryAuthenticatedOwnerExternalUserIdInput>();
        snapshot.LifecycleStatus.Should().Be(DeliveryApplication.WorkflowDeliveryLifecycleStatus.Active);
        snapshot.Installation!.Status.Should().Be(DeliveryApplication.WorkflowInstallationStatus.Ready);
        snapshot.Installation.AcceptanceInput.Should().NotBeNull();
        snapshot.Installation.AcceptanceInput!.Fields.Should().ContainKey("created_month")
            .WhoseValue.StringValue.Should().Be("period:2026-08:utc");
        snapshot.Installation.OperationId.Should().Be("installation-alpha:provision:a1");
        snapshot.Installation.ContinuationClaim.Should().NotBeNull();
        snapshot.Installation.ContinuationClaim!.ClaimId.Should().Be("claim-readiness-a1");
        snapshot.Installation.ContinuationClaim.ClaimantId.Should().Be("worker-alpha");
        snapshot.Installation.CapabilityAdmissionPlan.Should().NotBeNull();
        snapshot.Installation.AcceptanceRunId.Should().Be("acceptance-run-alpha");
        snapshot.Installation.ArtifactEvidence.Should().Equal("artifact-alpha");
        snapshot.Installation.ReadinessEvidence!.PublishedService.CommittedStateVersion.Should().Be(20);
        snapshot.Installation.ReadinessEvidence.BoundRevision.BindingRunId.Should().Be("binding-run-alpha");
        snapshot.Installation.ReadinessEvidence.Trigger.NoTrigger!.Ready.Should().BeTrue();
        snapshot.Installation.ReadinessEvidence.Trigger.Schedule.Should().BeNull();
        snapshot.Installation.ReadinessEvidence.AcceptanceRun.Status.Should()
            .Be(DeliveryApplication.WorkflowAcceptanceRunStatus.TerminalSuccess);
        snapshot.Installation.ReadinessEvidence.Artifacts.Should().ContainSingle().Which
            .VerificationStatus.Should().Be(
                DeliveryApplication.WorkflowInstallationArtifactVerificationStatus.Verified);
        reader.GetKeys.Should().BeEmpty();
        var query = reader.Queries.Should().ContainSingle().Subject;
        query.Filters.Should().Contain(filter =>
            filter.FieldPath == "id" &&
            Equals(filter.Value.RawValue, WorkflowDeliveryConventions.BuildActorId("delivery-alpha")));
        query.Filters.Should().Contain(filter =>
            filter.FieldPath == "target_scope_id" &&
            Equals(filter.Value.RawValue, "scope-alpha"));
    }

    [Fact]
    public async Task GetForScopeAsync_WhenLegacyPackageHasNoAcceptanceInput_ShouldMapEmptyInput()
    {
        var document = ValidDocument();
        document.Package.AcceptancePolicy.Input = null;
        var port = new ProjectionWorkflowDeliveryQueryPort(new RecordingReader
        {
            QueryResult = Result(document),
        });

        var snapshot = await port.GetForScopeAsync("delivery-alpha", "scope-alpha");

        snapshot.Should().NotBeNull();
        snapshot!.Package.AcceptancePolicy.Input.Literals.Fields.Should().BeEmpty();
        snapshot.Package.AcceptancePolicy.Input.Bindings.Should().BeEmpty();
        snapshot.Package.AcceptancePolicy.InputDeclared.Should().BeFalse();
    }

    [Fact]
    public async Task GetForScopeAsync_WhenInstallationAcceptanceInputIsMissing_ShouldPreserveAbsence()
    {
        var document = ValidDocument();
        document.Installation.AcceptanceInput = null;
        var port = new ProjectionWorkflowDeliveryQueryPort(new RecordingReader
        {
            QueryResult = Result(document),
        });

        var snapshot = await port.GetForScopeAsync("delivery-alpha", "scope-alpha");

        snapshot!.Installation!.AcceptanceInput.Should().BeNull();
    }

    [Fact]
    public async Task GetForScopeAsync_WhenAcceptanceBindingHasNoSource_ShouldRejectDocument()
    {
        var document = ValidDocument();
        document.Package.AcceptancePolicy.Input.Bindings.Add(
            new WorkflowDeliveryAcceptanceInputBinding { Key = "unsupported" });
        var port = new ProjectionWorkflowDeliveryQueryPort(new RecordingReader
        {
            QueryResult = Result(document),
        });

        var act = () => port.GetForScopeAsync("delivery-alpha", "scope-alpha");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*acceptance_policy input is invalid*");
    }

    [Fact]
    public async Task GetForScopeAsync_WhenAcceptanceDateProjectionIsUnspecified_ShouldRejectDocument()
    {
        var document = ValidDocument();
        document.Package.AcceptancePolicy.Input.Bindings[0].InstallationCreatedAtUtc.DateProjection =
            WorkflowDeliveryAcceptanceDateProjection.Unspecified;
        var port = new ProjectionWorkflowDeliveryQueryPort(new RecordingReader
        {
            QueryResult = Result(document),
        });

        var act = () => port.GetForScopeAsync("delivery-alpha", "scope-alpha");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*acceptance_policy input is invalid*");
    }

    [Fact]
    public async Task GetForScopeAsync_WhenStoreReturnsCrossScopeDocument_ShouldReturnNullWithoutMappingSource()
    {
        var document = ValidDocument();
        document.Package.SourceYaml = string.Empty;
        var reader = new RecordingReader
        {
            QueryResult = Result(document),
        };
        var port = new ProjectionWorkflowDeliveryQueryPort(reader);

        var snapshot = await port.GetForScopeAsync("delivery-alpha", "scope-beta");

        snapshot.Should().BeNull();
        reader.GetKeys.Should().BeEmpty();
        reader.Queries.Should().ContainSingle().Which.Filters.Should().Contain(filter =>
            filter.FieldPath == "target_scope_id" &&
            Equals(filter.Value.RawValue, "scope-beta"));
    }

    [Fact]
    public async Task FindByInstallationAsync_ShouldRequireBothScopeAndInstallationIdentity()
    {
        var reader = new RecordingReader
        {
            QueryResult = Result(ValidDocument()),
        };
        var port = new ProjectionWorkflowDeliveryQueryPort(reader);

        var snapshot = await port.FindByInstallationAsync("scope-alpha", "installation-alpha");

        snapshot.Should().NotBeNull();
        snapshot!.DeliveryId.Should().Be("delivery-alpha");
        var query = reader.Queries.Should().ContainSingle().Subject;
        query.Filters.Should().Contain(filter =>
            filter.FieldPath == "installation.scope_id" &&
            Equals(filter.Value.RawValue, "scope-alpha"));
        query.Filters.Should().Contain(filter =>
            filter.FieldPath == "installation.installation_id" &&
            Equals(filter.Value.RawValue, "installation-alpha"));
    }

    [Fact]
    public async Task GetForScopeAsync_WhenOwnerIsNyxIdNative_ShouldMapEmptySubjectTenant()
    {
        var document = ValidDocument();
        document.Installation.AuthenticatedOwner = AuthorizationOwner("nyxid", string.Empty);
        var port = new ProjectionWorkflowDeliveryQueryPort(new RecordingReader
        {
            QueryResult = Result(document),
        });

        var snapshot = await port.GetForScopeAsync("delivery-alpha", "scope-alpha");

        snapshot!.Installation!.AuthenticatedOwner.Should().NotBeNull();
        snapshot.Installation.AuthenticatedOwner!.SubjectPlatform.Should().Be("nyxid");
        snapshot.Installation.AuthenticatedOwner.SubjectTenant.Should().BeEmpty();
    }

    [Fact]
    public async Task GetForScopeAsync_WhenOwnerIsChannelNative_ShouldRequireSubjectTenant()
    {
        var document = ValidDocument();
        document.Installation.AuthenticatedOwner = AuthorizationOwner("lark", string.Empty);
        var port = new ProjectionWorkflowDeliveryQueryPort(new RecordingReader
        {
            QueryResult = Result(document),
        });

        var act = () => port.GetForScopeAsync("delivery-alpha", "scope-alpha");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*subject_tenant*");
    }

    [Theory]
    [InlineData(
        DeliveryApplication.WorkflowInstallationStatus.Accepted,
        "WORKFLOW_INSTALLATION_STATUS_ACCEPTED")]
    [InlineData(
        DeliveryApplication.WorkflowInstallationStatus.ProvisioningAccepted,
        "WORKFLOW_INSTALLATION_STATUS_PROVISIONING_ACCEPTED")]
    [InlineData(
        DeliveryApplication.WorkflowInstallationStatus.Failed,
        "WORKFLOW_INSTALLATION_STATUS_FAILED")]
    [InlineData(
        DeliveryApplication.WorkflowInstallationStatus.Ready,
        "WORKFLOW_INSTALLATION_STATUS_READY")]
    public async Task ListAsync_ShouldUseExactProtobufJsonEnumFieldForInstallationStatusFilter(
        DeliveryApplication.WorkflowInstallationStatus status,
        string expectedStorageValue)
    {
        var reader = new RecordingReader();
        var port = new ProjectionWorkflowDeliveryQueryPort(reader);

        await port.ListAsync(new DeliveryApplication.WorkflowDeliveryListQuery(
            InstallationStatus: status));

        reader.Queries.Should().ContainSingle().Which.Filters.Should().Contain(filter =>
            filter.FieldPath == "installation.status.keyword" &&
            Equals(filter.Value.RawValue, expectedStorageValue));
    }

    private static WorkflowDeliveryCurrentStateDocument ValidDocument() =>
        new()
        {
            Id = WorkflowDeliveryConventions.BuildActorId("delivery-alpha"),
            ActorId = WorkflowDeliveryConventions.BuildActorId("delivery-alpha"),
            StateVersion = 7,
            LastEventId = "event-seven",
            UpdatedAt = At(2),
            DeliveryId = "delivery-alpha",
            Package = new WorkflowPackageVersionSnapshot
            {
                PackageId = "package-alpha",
                PackageVersionId = "package-alpha@sha256-alpha",
                WorkflowName = "workflow-alpha",
                Version = "1",
                DisplayName = "Workflow Alpha",
                SourceYaml = "name: workflow-alpha\n",
                SourceHash = "sha256-alpha",
                PackageHash = "package-hash-alpha",
                AcceptancePolicy = new WorkflowDeliveryAcceptancePolicy
                {
                    Mode = WorkflowDeliveryAcceptanceMode.AutomaticPreview,
                    Input = new WorkflowDeliveryAcceptanceInputRecipe
                    {
                        Literals = new Struct
                        {
                            Fields =
                            {
                                ["dry_run"] = ProtobufValue.ForBool(true),
                            },
                        },
                        Bindings =
                        {
                            new WorkflowDeliveryAcceptanceInputBinding
                            {
                                Key = "created_month",
                                Prefix = "period:",
                                Suffix = ":utc",
                                InstallationCreatedAtUtc =
                                    new WorkflowDeliveryInstallationCreatedAtUtcInput
                                    {
                                        DateProjection =
                                            WorkflowDeliveryAcceptanceDateProjection.UtcYearMonth,
                                        DayOffset = -2,
                                    },
                            },
                            new WorkflowDeliveryAcceptanceInputBinding
                            {
                                Key = "owner_id",
                                AuthenticatedOwnerExternalUserId =
                                    new WorkflowDeliveryAuthenticatedOwnerExternalUserIdInput(),
                            },
                        },
                    },
                },
                CreatedBy = "admin-alpha",
                CreatedAtUtc = At(0),
            },
            TargetScopeId = "scope-alpha",
            ExpiresAtUtc = At(8),
            LifecycleStatus = WorkflowDeliveryLifecycleStatus.Active,
            CreatedBy = "admin-alpha",
            CreatedAtUtc = At(0),
            Installation = new WorkflowInstallationState
            {
                InstallationId = "installation-alpha",
                IdempotencyKey = "publish-alpha",
                ScopeId = "scope-alpha",
                TeamId = "team-alpha",
                TriggerIntent = new WorkflowDeliveryTriggerIntent
                {
                    Kind = WorkflowDeliveryTriggerKind.None,
                },
                SourceHash = "sha256-alpha",
                ResolvedHash = "resolved-alpha",
                ResolvedYaml = "name: workflow-alpha\n",
                Status = WorkflowInstallationStatus.Ready,
                Stage = "ready",
                PublishedServiceId = "service-alpha",
                RevisionId = "revision-alpha",
                BindingRunId = "binding-run-alpha",
                ReadinessEvidence = ReadyEvidence(),
                Attempt = 1,
                OperationId = "installation-alpha:provision:a1",
                CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan(),
                AcceptanceInput = new Struct
                {
                    Fields =
                    {
                        ["created_month"] = ProtobufValue.ForString("period:2026-08:utc"),
                        ["dry_run"] = ProtobufValue.ForBool(true),
                        ["owner_id"] = ProtobufValue.ForString("user-alpha"),
                    },
                },
                CreatedAtUtc = At(1),
                UpdatedAtUtc = At(1),
                ContinuationClaim = new WorkflowInstallationContinuationClaim
                {
                    ClaimId = "claim-readiness-a1",
                    ClaimantId = "worker-alpha",
                    ExpectedStatus = WorkflowInstallationStatus.ProvisioningAccepted,
                    Attempt = 1,
                    OperationId = "installation-alpha:provision:a1",
                    ClaimedAtUtc = At(1),
                    ExpiresAtUtc = At(2),
                },
            },
        };

    private static WorkflowDeliveryAuthorizationOwnerContext AuthorizationOwner(
        string platform,
        string tenant) =>
        new()
        {
            Owner = new AuthorizationOwnerIdentity
            {
                Authority = "nyxid",
                OwnerKind = AuthorizationOwnerKind.Personal,
                OwnerSubject = "user-alpha",
            },
            SubjectPlatform = platform,
            SubjectTenant = tenant,
            SubjectExternalUserId = "user-alpha",
            VerifiedBindingId = "binding-alpha",
        };

    private static WorkflowInstallationReadinessEvidence ReadyEvidence()
    {
        var evidence = new WorkflowInstallationReadinessEvidence
        {
            PublishedService = new WorkflowPublishedServiceReadinessEvidence
            {
                PublishedServiceId = "service-alpha",
                Committed = true,
                Runnable = true,
                CommittedStateVersion = 20,
            },
            BoundRevision = new WorkflowBoundRevisionReadinessEvidence
            {
                RevisionId = "revision-alpha",
                BindingRunId = "binding-run-alpha",
                Bound = true,
                CommittedStateVersion = 21,
            },
            Trigger = new WorkflowTriggerReadinessEvidence
            {
                Intent = new WorkflowDeliveryTriggerIntent
                {
                    Kind = WorkflowDeliveryTriggerKind.None,
                },
                NoTrigger = new WorkflowNoTriggerReadinessEvidence { Ready = true },
            },
            AcceptanceRun = new WorkflowAcceptanceRunReadinessEvidence
            {
                AcceptanceRunId = "acceptance-run-alpha",
                Status = WorkflowAcceptanceRunStatus.TerminalSuccess,
                CommittedStateVersion = 22,
            },
        };
        evidence.Artifacts.Add(new WorkflowInstallationArtifactEvidence
        {
            Kind = WorkflowInstallationArtifactKind.RunOutput,
            ArtifactId = "artifact-alpha",
            VerificationStatus = WorkflowInstallationArtifactVerificationStatus.Verified,
            VerificationReference = "verification-alpha",
            ContentDigest = "sha256-artifact-alpha",
        });
        return evidence;
    }

    private static ProjectionDocumentQueryResult<WorkflowDeliveryCurrentStateDocument> Result(
        params WorkflowDeliveryCurrentStateDocument[] documents) =>
        new() { Items = documents };

    private static Timestamp At(int hours) => Timestamp.FromDateTimeOffset(
        DateTimeOffset.Parse("2026-08-16T01:00:00Z").AddHours(hours));

    private sealed class RecordingReader
        : IProjectionDocumentReader<WorkflowDeliveryCurrentStateDocument, string>
    {
        public WorkflowDeliveryCurrentStateDocument? GetDocument { get; init; }

        public ProjectionDocumentQueryResult<WorkflowDeliveryCurrentStateDocument> QueryResult { get; init; } =
            ProjectionDocumentQueryResult<WorkflowDeliveryCurrentStateDocument>.Empty;

        public List<string> GetKeys { get; } = [];

        public List<ProjectionDocumentQuery> Queries { get; } = [];

        public Task<WorkflowDeliveryCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            GetKeys.Add(key);
            return Task.FromResult(GetDocument);
        }

        public Task<ProjectionDocumentQueryResult<WorkflowDeliveryCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult(QueryResult);
        }
    }
}
