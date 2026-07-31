using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.GAgentService.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Locks in the HTTP-handler invariants for member-first endpoints:
///
/// - Each handler defers to <see cref="IStudioMemberService"/> only.
/// - Scope-access guard short-circuits with 403 before any service call.
/// - Domain validation failures from the service map to 400 with a stable
///   error code Studio's frontend can switch on.
/// - GET endpoints map "no document" to 404 (not 200 with a null body).
/// </summary>
public sealed class StudioMemberEndpointsTests
{
    private const string ScopeId = "scope-1";

    [Fact]
    public async Task HandleCreateAsync_ShouldReturnCreated_OnSuccess()
    {
        var service = new RecordingMemberService
        {
            CreateResponse = NewSummary(),
        };

        var result = await InvokeHandle<IResult>(
            "HandleCreateAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow),
            service,
            CancellationToken.None);

        var created = result.Should().BeOfType<Created<StudioMemberSummaryResponse>>().Subject;
        created.Location.Should().Be($"/api/scopes/{ScopeId}/members/{NewSummary().MemberId}");
        created.Value!.LifecycleStage.Should().Be(MemberLifecycleStageNames.Created);
        created.Value.ImplementationRef.Should().BeNull();
        service.CreateInvoked.Should().BeTrue();
        service.CreateRequest!.ImplementationRef.Should().BeNull();
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldReturnTypedBadRequest_WhenImplementationRefIsPresent()
    {
        var service = new RecordingMemberService
        {
            CreateException = new StudioMemberCreateImplementationRefNotAllowedException(ScopeId),
        };

        var result = await InvokeHandle<IResult>(
            "HandleCreateAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                ImplementationRef: new StudioMemberImplementationRefResponse(
                    ImplementationKind: MemberImplementationKindNames.Workflow,
                    WorkflowId: "wf-alpha")),
            service,
            CancellationToken.None);

        AssertBadRequestResult(
            result,
            StudioMemberCreateImplementationRefNotAllowedException.ErrorCode,
            expectedField: "implementationRef",
            expectedScopeId: ScopeId);
        service.CreateInvoked.Should().BeTrue();
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldReturnBadRequest_OnDomainError()
    {
        var service = new RecordingMemberService
        {
            CreateException = new InvalidOperationException("displayName is required."),
        };

        var result = await InvokeHandle<IResult>(
            "HandleCreateAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: string.Empty,
                ImplementationKind: MemberImplementationKindNames.Workflow),
            service,
            CancellationToken.None);

        // BadRequest<TAnonymousType> — the anonymous type is internal, so we
        // assert via the open generic shape rather than nailing the closed type.
        result.GetType().Name.Should().StartWith("BadRequest");
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldReturnForbidden_WhenScopeAccessDenied()
    {
        var service = new RecordingMemberService();

        var result = await InvokeHandle<IResult>(
            "HandleCreateAsync",
            CreateAuthenticatedContext("other-scope"),
            ScopeId,
            new CreateStudioMemberRequest(
                DisplayName: "Alpha",
                ImplementationKind: MemberImplementationKindNames.Workflow),
            service,
            CancellationToken.None);

        // Service must not be touched after the guard short-circuits.
        service.CreateInvoked.Should().BeFalse();
        // The denied result is JSON with statusCode 403; assertion via shape.
        AssertIsJsonStatus(result, expectedStatus: StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task HandleListAsync_ShouldReturnOk_OnSuccess()
    {
        var service = new RecordingMemberService
        {
            ListResponse = new StudioMemberRosterResponse(ScopeId, [NewSummary()]),
        };

        var result = await InvokeHandle<IResult>(
            "HandleListAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            service,
            (int?)null,
            (string?)null,
            CancellationToken.None);

        result.Should().BeOfType<Ok<StudioMemberRosterResponse>>()
            .Which.Value!.Members.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleGetAsync_ShouldReturnTyped404_WhenMemberMissing()
    {
        // GetAsync now throws StudioMemberNotFoundException for missing
        // members; the endpoint returns the same typed 404 body that
        // bind / get-binding do — three endpoints, one 404 shape.
        var service = new RecordingMemberService
        {
            GetException = new StudioMemberNotFoundException(ScopeId, "m-missing"),
        };

        var result = await InvokeHandle<IResult>(
            "HandleGetAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-missing",
            service,
            CancellationToken.None);

        var statusCode = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        statusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleGetAsync_ShouldReturnOk_WhenServiceReturnsDetail()
    {
        var detail = new StudioMemberDetailResponse(NewSummary(), null, null);
        var service = new RecordingMemberService
        {
            GetResponse = detail,
        };

        var result = await InvokeHandle<IResult>(
            "HandleGetAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-1",
            service,
            CancellationToken.None);

        result.Should().BeOfType<Ok<StudioMemberDetailResponse>>()
            .Which.Value.Should().BeSameAs(detail);
    }

    [Fact]
    public async Task HandlePatchAsync_ShouldReturnAccepted_OnTeamPatch()
    {
        var request = new StudioMemberEndpoints.StudioMemberPatchBody
        {
            TeamId = System.Text.Json.JsonSerializer.SerializeToElement("team-alpha"),
        };
        var service = new RecordingMemberService();

        var result = await InvokeHandle<IResult>(
            "HandlePatchAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-alpha",
            request,
            service,
            CancellationToken.None);

        var accepted = result.Should().BeOfType<Accepted<StudioMemberCommandResponse>>().Subject;
        accepted.Location.Should().Be($"/api/scopes/{ScopeId}/members/m-alpha");
        accepted.Value!.Status.Should().Be(StudioMemberCommandStatusNames.Accepted);
        service.UpdateInvoked.Should().BeTrue();
        service.UpdateRequest!.TeamId.HasValue.Should().BeTrue();
        service.UpdateRequest.TeamId.Value.Should().Be("team-alpha");
    }

    [Fact]
    public async Task HandlePatchAsync_ShouldMapImplementationRefPatch()
    {
        var implementationRef = new StudioMemberImplementationRefResponse(
            ImplementationKind: MemberImplementationKindNames.Workflow,
            WorkflowId: "wf-alpha");
        var request = new StudioMemberEndpoints.StudioMemberPatchBody
        {
            ImplementationRef = System.Text.Json.JsonSerializer.SerializeToElement(implementationRef),
        };
        var service = new RecordingMemberService();

        var result = await InvokeHandle<IResult>(
            "HandlePatchAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-alpha",
            request,
            service,
            CancellationToken.None);

        var accepted = result.Should().BeOfType<Accepted<StudioMemberCommandResponse>>().Subject;
        accepted.Location.Should().Be($"/api/scopes/{ScopeId}/members/m-alpha");
        accepted.Value!.Status.Should().Be(StudioMemberCommandStatusNames.Accepted);
        service.UpdateInvoked.Should().BeTrue();
        service.UpdateRequest!.ImplementationRef.HasValue.Should().BeTrue();
        service.UpdateRequest.ImplementationRef.Value.Should().Be(implementationRef);
    }

    [Fact]
    public async Task HandlePatchAsync_ShouldForwardDisplayNamePatch()
    {
        var service = new RecordingMemberService();

        var result = await InvokeHandle<IResult>(
            "HandlePatchAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-alpha",
            new StudioMemberEndpoints.StudioMemberPatchBody
            {
                DisplayName = JsonSerializer.SerializeToElement("Renamed Workflow"),
            },
            service,
            CancellationToken.None);

        var accepted = result.Should().BeOfType<Accepted<StudioMemberCommandResponse>>().Subject;
        accepted.Location.Should().Be($"/api/scopes/{ScopeId}/members/m-alpha");
        accepted.Value!.Status.Should().Be(StudioMemberCommandStatusNames.Accepted);
        service.UpdateRequest!.DisplayName.HasValue.Should().BeTrue();
        service.UpdateRequest.DisplayName.Value.Should().Be("Renamed Workflow");
        service.UpdateRequest.TeamId.HasValue.Should().BeFalse();
        service.UpdateRequest.ImplementationRef.HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task HandlePatchAsync_ShouldRejectNonStringDisplayName()
    {
        var service = new RecordingMemberService();

        var result = await InvokeHandle<IResult>(
            "HandlePatchAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-alpha",
            new StudioMemberEndpoints.StudioMemberPatchBody
            {
                DisplayName = JsonSerializer.SerializeToElement(42),
            },
            service,
            CancellationToken.None);

        AssertBadRequestResult(result, "INVALID_STUDIO_MEMBER_REQUEST");
        service.UpdateInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task HandlePatchAsync_ShouldReturnBadRequest_OnValidationError()
    {
        var service = new RecordingMemberService
        {
            UpdateException = new InvalidOperationException("teamId must not be empty."),
        };

        var result = await InvokeHandle<IResult>(
            "HandlePatchAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-alpha",
            new StudioMemberEndpoints.StudioMemberPatchBody
            {
                TeamId = System.Text.Json.JsonSerializer.SerializeToElement("team-alpha"),
            },
            service,
            CancellationToken.None);

        AssertBadRequestResult(result, "INVALID_STUDIO_MEMBER_REQUEST");
    }

    [Fact]
    public async Task HandlePatchAsync_ShouldReturnTyped404_WhenMemberMissing()
    {
        var service = new RecordingMemberService
        {
            UpdateException = new StudioMemberNotFoundException(ScopeId, "m-missing"),
        };

        var result = await InvokeHandle<IResult>(
            "HandlePatchAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-missing",
            new StudioMemberEndpoints.StudioMemberPatchBody
            {
                TeamId = System.Text.Json.JsonSerializer.SerializeToElement("team-alpha"),
            },
            service,
            CancellationToken.None);

        var statusCode = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        statusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleDeleteAsync_ShouldReturnAccepted_OnSuccess()
    {
        var service = new RecordingMemberService();

        var result = await InvokeHandle<IResult>(
            "HandleDeleteAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-alpha",
            service,
            CancellationToken.None);

        var accepted = result.Should().BeOfType<Accepted<StudioMemberCommandResponse>>().Subject;
        accepted.Location.Should().Be($"/api/scopes/{ScopeId}/members/m-alpha");
        accepted.Value!.Status.Should().Be(StudioMemberCommandStatusNames.DeleteAccepted);
        service.DeleteInvoked.Should().BeTrue();
        service.DeleteScopeId.Should().Be(ScopeId);
        service.DeleteMemberId.Should().Be("m-alpha");
    }

    [Fact]
    public async Task HandleDeleteAsync_ShouldReturnTyped404_WhenMemberMissing()
    {
        var service = new RecordingMemberService
        {
            DeleteException = new StudioMemberNotFoundException(ScopeId, "m-missing"),
        };

        var result = await InvokeHandle<IResult>(
            "HandleDeleteAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-missing",
            service,
            CancellationToken.None);

        AssertNotFoundResult(result, "STUDIO_MEMBER_NOT_FOUND");
    }

    [Fact]
    public async Task HandleDeleteAsync_ShouldReturnForbidden_WhenScopeAccessDenied()
    {
        var service = new RecordingMemberService();

        var result = await InvokeHandle<IResult>(
            "HandleDeleteAsync",
            CreateAuthenticatedContext("other-scope"),
            ScopeId,
            "m-alpha",
            service,
            CancellationToken.None);

        service.DeleteInvoked.Should().BeFalse();
        AssertIsJsonStatus(result, expectedStatus: StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task DeleteAccepted_WhenCommittedDeleteIsProjected_ShouldMakeDetail404AndRosterEmpty()
    {
        var actorId = StudioMemberConventions.BuildActorId(ScopeId, "m-alpha");
        var store = new InMemoryProjectionDocumentStore<StudioMemberCurrentStateDocument, string>(
            keySelector: model => model.Id);
        await store.UpsertAsync(new StudioMemberCurrentStateDocument
        {
            Id = actorId,
            ActorId = actorId,
            StateVersion = 7,
            LastEventId = "evt-7",
            UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-29T00:00:07Z")),
            MemberId = "m-alpha",
            ScopeId = ScopeId,
            DisplayName = "Alpha",
            ImplementationKind = MemberImplementationKindNames.Workflow,
            LifecycleStage = MemberLifecycleStageNames.BindReady,
            PublishedServiceId = "svc-alpha",
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-28T00:00:00Z")),
        });
        var service = new ProjectionBackedMemberService(new ProjectionStudioMemberQueryPort(store));

        var acceptedResult = await InvokeHandle<IResult>(
            "HandleDeleteAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-alpha",
            service,
            CancellationToken.None);

        var accepted = acceptedResult.Should().BeOfType<Accepted<StudioMemberCommandResponse>>().Subject;
        accepted.Value!.Status.Should().Be(StudioMemberCommandStatusNames.DeleteAccepted);

        await store.DeleteAsync(new ProjectionDocumentDeleteMarker(
            actorId,
            actorId,
            8,
            "evt-8-delete",
            DateTimeOffset.Parse("2026-07-29T00:00:08Z")));

        var getResult = await InvokeHandle<IResult>(
            "HandleGetAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-alpha",
            service,
            CancellationToken.None);
        var listResult = await InvokeHandle<IResult>(
            "HandleListAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            service,
            (int?)null,
            (string?)null,
            CancellationToken.None);

        AssertNotFoundResult(getResult, "STUDIO_MEMBER_NOT_FOUND");
        listResult.Should().BeOfType<Ok<StudioMemberRosterResponse>>()
            .Which.Value!.Members.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleBindAsync_ShouldReturnAccepted_OnSuccess()
    {
        var binding = new StudioMemberBindingAcceptedResponse(
            Status: StudioMemberBindingRunStatusNames.Accepted,
            BindingRunId: "bind-1",
            ScopeId: ScopeId,
            MemberId: "m-1");
        var service = new RecordingMemberService
        {
            BindResponse = binding,
        };

        var result = await InvokeHandle<IResult>(
            "HandleBindAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-1",
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec("workflow-stable-id", ["w:"])),
            service,
            CancellationToken.None);

        result.Should().BeOfType<Accepted<StudioMemberBindingAcceptedResponse>>()
            .Which.Value.Should().BeSameAs(binding);
    }

    [Fact]
    public async Task HandleBindAsync_ShouldMapAndScrubExplicitRequestConfirmations()
    {
        var service = new RecordingMemberService
        {
            BindResponse = new StudioMemberBindingAcceptedResponse(
                StudioMemberBindingRunStatusNames.Accepted,
                "bind-alpha",
                ScopeId,
                "m-alpha"),
        };
        var http = CreateAuthenticatedContext(ScopeId);
        ((ClaimsIdentity)http.User.Identity!).AddClaim(new Claim("sub", "caller-alpha"));

        await InvokeHandle<IResult>(
            "HandleBindAsync",
            http,
            ScopeId,
            "m-alpha",
            new UpdateStudioMemberBindingRequest(
                RevisionId: "rev-alpha",
                Workflow: new StudioMemberWorkflowBindingSpec("wf-alpha", ["name: wf-alpha"]))
            {
                ExplicitRequestConfirmations =
                [
                    new NyxIdExplicitRequestConfirmationInput(
                        "wf-alpha/request-alpha",
                        "digest-alpha",
                        "read_only"),
                ],
            },
            service,
            CancellationToken.None);

        service.BindRequest.Should().NotBeNull();
        service.BindRequest!.RevisionId.Should().Be("rev-alpha");
        service.BindRequest.Workflow!.WorkflowId.Should().Be("wf-alpha");
        service.BindRequest.ExplicitRequestConfirmations.Should().BeNull();
        var admission = service.BindRequest.CapabilityAdmission;
        admission.Should().NotBeNull();
        admission!.CallerId.Should().Be("caller-alpha");
        admission.ExplicitRequestConfirmations.Should().ContainSingle().Which.AttestedRisk.Should()
            .Be(NyxIdOperationRisk.ReadOnly);
    }

    [Fact]
    public async Task HandleBindAsync_WithNullExplicitRequestConfirmation_ShouldReturnTypedBadRequestWithoutDispatch()
    {
        var service = new RecordingMemberService();
        var http = CreateAuthenticatedContext(ScopeId);
        http.Response.Body = new MemoryStream();

        var result = await InvokeHandle<IResult>(
            "HandleBindAsync",
            http,
            ScopeId,
            "m-alpha",
            new UpdateStudioMemberBindingRequest(
                RevisionId: "rev-alpha",
                Workflow: new StudioMemberWorkflowBindingSpec("wf-alpha", ["name: wf-alpha"]))
            {
                ExplicitRequestConfirmations = [null!],
            },
            service,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(http.Response.Body);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.RootElement.GetProperty("code").GetString().Should()
            .Be("INVALID_EXPLICIT_REQUEST_CONFIRMATION");
        service.BindRequest.Should().BeNull();
    }

    [Fact]
    public async Task HandleBindAsync_WithMalformedAuthorizationAndDelegation_ShouldRejectWithoutDispatch()
    {
        var service = new RecordingMemberService();
        var http = CreateAuthenticatedContext(ScopeId);
        http.Request.Headers.Authorization = "Bearer token with spaces";
        http.Request.Headers["X-NyxID-Delegation-Token"] = "delegation-token";

        var result = await InvokeHandle<IResult>(
            "HandleBindAsync",
            http,
            ScopeId,
            "m-alpha",
            new UpdateStudioMemberBindingRequest(
                RevisionId: "rev-alpha",
                Workflow: new StudioMemberWorkflowBindingSpec("wf-alpha", ["name: wf-alpha\nsteps: []\n"])),
            service,
            CancellationToken.None);

        AssertBadRequestResult(result, "INVALID_WORKFLOW_CALLER_CREDENTIAL");
        service.BindRequest.Should().BeNull();
    }

    [Fact]
    public async Task HandleBindAsync_ShouldReturnBadRequest_OnDomainError()
    {
        var service = new RecordingMemberService
        {
            BindException = new InvalidOperationException("workflow yamls are required."),
        };

        var result = await InvokeHandle<IResult>(
            "HandleBindAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-1",
            new UpdateStudioMemberBindingRequest(),
            service,
            CancellationToken.None);

        // BadRequest<TAnonymousType> — the anonymous type is internal, so we
        // assert via the open generic shape rather than nailing the closed type.
        result.GetType().Name.Should().StartWith("BadRequest");
    }

    [Fact]
    public async Task HandleBindAsync_ShouldReturnTypedSafeReadiness_WhenCapabilityAdmissionFails()
    {
        const string secret = "Bearer endpoint-secret-value";
        var readiness = new ExternalCapabilityReadiness
        {
            ExecutionMode = ExternalCapabilityExecutionMode.Interactive,
            Status = ExternalCapabilityReadinessStatus.OperationSelectionRequired,
            SelectedSelector = new ExternalWorkflowCapabilitySelector
            {
                NyxIdOperation = new NyxIdOperationSelector
                {
                    UserServiceId = "us-alpha",
                    EndpointId = "get-resource",
                },
            },
            SelectedCapability = new ExternalWorkflowCapabilityRef
            {
                NyxIdUserService = new NyxIdUserServiceCapabilityRef
                {
                    UserServiceId = "us-alpha",
                    ServiceSlugSnapshot = secret,
                    EndpointId = "get-resource",
                    HttpMethod = "GET",
                    PathTemplate = "/internal/{id}",
                    ContractDigest = secret,
                },
            },
        };
        readiness.Blockers.Add(new ExternalCapabilityBlocker
        {
            Status = ExternalCapabilityReadinessStatus.OperationSelectionRequired,
            Code = "OPERATION_NOT_ALLOWLISTED",
            SafeMessage = "Select an operation published through the allowlist.",
        });
        readiness.Remediations.Add(new ExternalCapabilityRemediation
        {
            ActionKind = ExternalCapabilityRemediationActionKind.SelectOperation,
            Label = "Select operation",
            TrustedLocator = "nyxid:services",
        });
        readiness.Sources.Add(new ExternalCapabilitySourceStamp
        {
            SourceKind = ExternalCapabilitySourceKind.NyxIdMcpConfig,
            SourceId = "nyxid-mcp-config:caller:nyx-user-alpha",
            SourceVersion = 0,
            ObservedAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 7, 28, 1, 0, 0, TimeSpan.Zero)),
            FreshUntil = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 7, 28, 1, 5, 0, TimeSpan.Zero)),
            ContentDigest = secret,
        });
        var service = new RecordingMemberService
        {
            BindException = new WorkflowExternalCapabilityAdmissionException(readiness),
        };
        var http = CreateAuthenticatedContext(ScopeId);
        http.Response.Body = new MemoryStream();

        var result = await InvokeHandle<IResult>(
            "HandleBindAsync",
            http,
            ScopeId,
            "m-1",
            new UpdateStudioMemberBindingRequest(),
            service,
            CancellationToken.None);

        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(http.Response.Body);
        var root = body.RootElement;
        root.GetProperty("code").GetString().Should()
            .Be("STUDIO_MEMBER_EXTERNAL_CAPABILITY_NOT_READY");
        root.GetProperty("message").GetString().Should()
            .Be("External workflow capability admission failed.");
        var readinessJson = root.GetProperty("readiness");
        readinessJson.GetProperty("status").GetString().Should()
            .Be("operation_selection_required");
        var selected = readinessJson.GetProperty("selectedCapability");
        selected.GetProperty("userServiceId").GetString().Should().Be("us-alpha");
        selected.GetProperty("endpointId").GetString().Should().Be("get-resource");
        selected.GetProperty("operationId").ValueKind.Should().Be(JsonValueKind.Null);
        readinessJson.GetProperty("blockers")[0].GetProperty("code").GetString().Should()
            .Be("OPERATION_NOT_ALLOWLISTED");
        readinessJson.GetProperty("remediations")[0].GetProperty("actionKind").GetString().Should()
            .Be("select_operation");
        readinessJson.GetProperty("sources")[0].GetProperty("sourceKind").GetString().Should()
            .Be("nyx_id_mcp_config");

        var responseJson = root.GetRawText();
        responseJson.Should().NotContain(secret);
        responseJson.Should().NotContain("contractDigest");
        responseJson.Should().NotContain("pathTemplate");
        responseJson.Should().NotContain("contentDigest");
    }

    [Fact]
    public async Task HandleGetBindingAsync_ShouldReturnOk_WithNullBinding_WhenMemberExistsButNeverBound()
    {
        // Disambiguates the prior 404 shape: a member that exists but has
        // never been bound is NOT missing (which has its own typed 404).
        // It's a member with a null binding — surface as 200 with the
        // wrapper and let the frontend dispatch on `lastBinding === null`.
        var service = new RecordingMemberService
        {
            GetBindingResponse = null,
        };

        var result = await InvokeHandle<IResult>(
            "HandleGetBindingAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-1",
            service,
            CancellationToken.None);

        result.Should().BeOfType<Ok<StudioMemberBindingViewResponse>>()
            .Which.Value!.LastBinding.Should().BeNull();
    }

    [Fact]
    public async Task HandleGetBindingAsync_ShouldReturnOk_WhenServiceReturnsBinding()
    {
        var contract = new StudioMemberBindingContractResponse(
            "member-m-1", "rev-1", MemberImplementationKindNames.Workflow, DateTimeOffset.UtcNow);
        var service = new RecordingMemberService
        {
            GetBindingResponse = contract,
        };

        var result = await InvokeHandle<IResult>(
            "HandleGetBindingAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-1",
            service,
            CancellationToken.None);

        result.Should().BeOfType<Ok<StudioMemberBindingViewResponse>>()
            .Which.Value!.LastBinding.Should().BeSameAs(contract);
    }

    [Fact]
    public async Task HandleGetBindingRunAsync_ShouldReturnOk_WhenServiceReturnsRunStatus()
    {
        var run = new StudioMemberBindingRunStatusResponse(
            BindingRunId: "bind-1",
            ScopeId: ScopeId,
            MemberId: "member-1",
            Status: StudioMemberBindingRunStatusNames.PlatformBindingPending,
            StateVersion: 7,
            UpdatedAt: DateTimeOffset.UtcNow);
        var service = new RecordingMemberService
        {
            GetBindingRunResponse = run,
        };

        var result = await InvokeHandle<IResult>(
            "HandleGetBindingRunAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-1",
            "bind-1",
            service,
            CancellationToken.None);

        result.Should().BeOfType<Ok<StudioMemberBindingRunStatusResponse>>()
            .Which.Value.Should().BeSameAs(run);
    }

    [Fact]
    public async Task HandleGetBindingRunAsync_ShouldReturnTyped404_WhenBindingRunMissing()
    {
        var service = new RecordingMemberService
        {
            GetBindingRunException = new StudioMemberBindingRunNotFoundException(
                ScopeId,
                "m-1",
                "bind-missing"),
        };

        var result = await InvokeHandle<IResult>(
            "HandleGetBindingRunAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-1",
            "bind-missing",
            service,
            CancellationToken.None);

        AssertNotFoundResult(result, "STUDIO_MEMBER_BINDING_RUN_NOT_FOUND");
    }

    [Fact]
    public async Task HandleGetBindingRunAsync_ShouldReturnBadRequest_OnDomainError()
    {
        var service = new RecordingMemberService
        {
            GetBindingRunException = new InvalidOperationException("bindingRunId is required."),
        };

        var result = await InvokeHandle<IResult>(
            "HandleGetBindingRunAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-1",
            "",
            service,
            CancellationToken.None);

        AssertBadRequestResult(result, "INVALID_STUDIO_MEMBER_REQUEST");
    }

    [Fact]
    public async Task HandleGetBindingRunAsync_ShouldReturnForbidden_WhenScopeAccessDenied()
    {
        var service = new RecordingMemberService();

        var result = await InvokeHandle<IResult>(
            "HandleGetBindingRunAsync",
            CreateAuthenticatedContext("other-scope"),
            ScopeId,
            "m-1",
            "bind-1",
            service,
            CancellationToken.None);

        AssertIsJsonStatus(result, expectedStatus: StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task HandleGetEndpointContractAsync_ShouldReturnOk_OnSuccess()
    {
        var contract = NewContract();
        var service = new RecordingMemberService { EndpointContractResponse = contract };

        var result = await InvokeHandle<IResult>(
            "HandleGetEndpointContractAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-1",
            "chat",
            service,
            CancellationToken.None);

        result.Should().BeOfType<Ok<StudioMemberEndpointContractResponse>>()
            .Which.Value.Should().BeSameAs(contract);
    }

    [Fact]
    public async Task HandleGetEndpointContractAsync_ShouldReturnNotFound_WhenServiceReturnsNull()
    {
        // Service returns null for "exists, but no such endpoint on the
        // member's published service" — the endpoint maps that to a typed
        // 404 the frontend can switch on, distinct from the 404 for a
        // missing member itself.
        var service = new RecordingMemberService { EndpointContractResponse = null };

        var result = await InvokeHandle<IResult>(
            "HandleGetEndpointContractAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-1",
            "no-such-ep",
            service,
            CancellationToken.None);

        AssertNotFoundResult(result, "STUDIO_MEMBER_ENDPOINT_CONTRACT_NOT_FOUND");
    }

    [Fact]
    public async Task HandleGetEndpointContractAsync_ShouldReturnTyped404_WhenMemberMissing()
    {
        var service = new RecordingMemberService
        {
            EndpointContractException = new StudioMemberNotFoundException(ScopeId, "m-missing"),
        };

        var result = await InvokeHandle<IResult>(
            "HandleGetEndpointContractAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-missing",
            "chat",
            service,
            CancellationToken.None);

        var statusCode = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        statusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleGetEndpointContractAsync_ShouldReturnBadRequest_OnDomainError()
    {
        var service = new RecordingMemberService
        {
            EndpointContractException = new InvalidOperationException("member 'm-1' has no published service yet"),
        };

        var result = await InvokeHandle<IResult>(
            "HandleGetEndpointContractAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-1",
            "chat",
            service,
            CancellationToken.None);

        AssertBadRequestResult(result, "INVALID_STUDIO_MEMBER_ENDPOINT_CONTRACT_REQUEST");
    }

    [Fact]
    public async Task HandleGetEndpointContractAsync_ShouldReturnForbidden_WhenScopeAccessDenied()
    {
        var service = new RecordingMemberService();

        var result = await InvokeHandle<IResult>(
            "HandleGetEndpointContractAsync",
            CreateAuthenticatedContext("other-scope"),
            ScopeId,
            "m-1",
            "chat",
            service,
            CancellationToken.None);

        // EndpointContractException being null without a guard would NRE; the
        // guard must short-circuit before the service is touched.
        AssertIsJsonStatus(result, expectedStatus: StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task HandleActivateBindingRevisionAsync_ShouldReturnOk_OnSuccess()
    {
        var activation = new StudioMemberBindingActivationResponse(
            ScopeId, "m-1", "member-m-1", "Alpha", "rev-1");
        var service = new RecordingMemberService { ActivateResponse = activation };

        var result = await InvokeHandle<IResult>(
            "HandleActivateBindingRevisionAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-1",
            "rev-1",
            service,
            CancellationToken.None);

        result.Should().BeOfType<Ok<StudioMemberBindingActivationResponse>>()
            .Which.Value.Should().BeSameAs(activation);
    }

    [Fact]
    public async Task HandleActivateBindingRevisionAsync_ShouldReturnTyped404_WhenMemberMissing()
    {
        var service = new RecordingMemberService
        {
            ActivateException = new StudioMemberNotFoundException(ScopeId, "m-missing"),
        };

        var result = await InvokeHandle<IResult>(
            "HandleActivateBindingRevisionAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-missing",
            "rev-1",
            service,
            CancellationToken.None);

        var statusCode = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        statusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleActivateBindingRevisionAsync_ShouldReturnBadRequest_OnDomainError()
    {
        // E.g. revision is retired — service throws InvalidOperationException.
        var service = new RecordingMemberService
        {
            ActivateException = new InvalidOperationException("Revision 'rev-x' is retired and cannot be activated."),
        };

        var result = await InvokeHandle<IResult>(
            "HandleActivateBindingRevisionAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-1",
            "rev-x",
            service,
            CancellationToken.None);

        AssertBadRequestResult(result, "INVALID_STUDIO_MEMBER_BINDING_ACTIVATION_REQUEST");
    }

    [Fact]
    public async Task HandleActivateBindingRevisionAsync_ShouldReturnForbidden_WhenScopeAccessDenied()
    {
        var service = new RecordingMemberService();

        var result = await InvokeHandle<IResult>(
            "HandleActivateBindingRevisionAsync",
            CreateAuthenticatedContext("other-scope"),
            ScopeId,
            "m-1",
            "rev-1",
            service,
            CancellationToken.None);

        // ActivateException being null without a guard would NRE; the guard
        // must short-circuit before the service is touched.
        AssertIsJsonStatus(result, expectedStatus: StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task HandleRetireBindingRevisionAsync_ShouldReturnOk_OnSuccess()
    {
        var retire = new StudioMemberBindingRevisionActionResponse(
            ScopeId, "m-1", "member-m-1", "rev-1", "retired");
        var service = new RecordingMemberService { RetireResponse = retire };

        var result = await InvokeHandle<IResult>(
            "HandleRetireBindingRevisionAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-1",
            "rev-1",
            service,
            CancellationToken.None);

        result.Should().BeOfType<Ok<StudioMemberBindingRevisionActionResponse>>()
            .Which.Value.Should().BeSameAs(retire);
    }

    [Fact]
    public async Task HandleRetireBindingRevisionAsync_ShouldReturnTyped404_WhenMemberMissing()
    {
        var service = new RecordingMemberService
        {
            RetireException = new StudioMemberNotFoundException(ScopeId, "m-missing"),
        };

        var result = await InvokeHandle<IResult>(
            "HandleRetireBindingRevisionAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-missing",
            "rev-1",
            service,
            CancellationToken.None);

        var statusCode = result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;
        statusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleRetireBindingRevisionAsync_ShouldReturnBadRequest_OnDomainError()
    {
        var service = new RecordingMemberService
        {
            RetireException = new InvalidOperationException("Revision 'rev-x' was not found."),
        };

        var result = await InvokeHandle<IResult>(
            "HandleRetireBindingRevisionAsync",
            CreateAuthenticatedContext(ScopeId),
            ScopeId,
            "m-1",
            "rev-x",
            service,
            CancellationToken.None);

        AssertBadRequestResult(result, "INVALID_STUDIO_MEMBER_BINDING_REVISION_REQUEST");
    }

    [Fact]
    public async Task HandleRetireBindingRevisionAsync_ShouldReturnForbidden_WhenScopeAccessDenied()
    {
        var service = new RecordingMemberService();

        var result = await InvokeHandle<IResult>(
            "HandleRetireBindingRevisionAsync",
            CreateAuthenticatedContext("other-scope"),
            ScopeId,
            "m-1",
            "rev-1",
            service,
            CancellationToken.None);

        // RetireException being null without a guard would NRE; the guard
        // must short-circuit before the service is touched.
        AssertIsJsonStatus(result, expectedStatus: StatusCodes.Status403Forbidden);
    }

    private static StudioMemberEndpointContractResponse NewContract() => new(
        ScopeId: ScopeId,
        MemberId: "m-1",
        PublishedServiceId: "member-m-1",
        EndpointId: "chat",
        InvokePath: $"/api/scopes/{ScopeId}/members/m-1/invoke/chat:stream",
        Method: "POST",
        RequestContentType: "application/json",
        ResponseContentType: "text/event-stream",
        RequestTypeUrl: "type.googleapis.com/x.Request",
        ResponseTypeUrl: "type.googleapis.com/x.Response",
        SupportsSse: true,
        SupportsWebSocket: false,
        SupportsAguiFrames: true,
        StreamFrameFormat: "agui",
        SmokeTestSupported: true,
        DefaultSmokeInputMode: "prompt",
        DefaultSmokePrompt: "Hello from Studio Bind.",
        SampleRequestJson: null,
        DeploymentStatus: "Active",
        RevisionId: "rev-1",
        InvocationReadiness: new StudioMemberInvocationReadinessResponse(
            CanInvoke: true,
            Status: StudioMemberInvocationReadinessStatusNames.Ready,
            ReasonCode: StudioMemberInvocationReadinessStatusNames.Ready,
            Message: "Member endpoint is ready for invocation.",
            RevisionId: "rev-1"));

    private static StudioMemberSummaryResponse NewSummary() => new(
        MemberId: "m-1",
        ScopeId: ScopeId,
        DisplayName: "Alpha",
        Description: string.Empty,
        ImplementationKind: MemberImplementationKindNames.Workflow,
        LifecycleStage: MemberLifecycleStageNames.Created,
        PublishedServiceId: "member-m-1",
        LastBoundRevisionId: null,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    private static HttpContext CreateAuthenticatedContext(string claimedScopeId)
    {
        var identity = new ClaimsIdentity(
            [new Claim("scope_id", claimedScopeId)],
            "test");
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "true",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .BuildServiceProvider();
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
            RequestServices = services,
        };
    }

    private static void AssertIsJsonStatus(IResult result, int expectedStatus)
    {
        // ASP.NET Core's Results.Json yields a JsonHttpResult<T> whose
        // StatusCode property exposes the configured status. We check by
        // reflection so this test stays decoupled from the precise generic.
        var statusCodeProperty = result.GetType().GetProperty("StatusCode");
        var statusCode = statusCodeProperty?.GetValue(result) as int?;
        statusCode.Should().Be(expectedStatus,
            because: $"expected JSON result with status {expectedStatus} but got {result.GetType().Name}");
    }

    private static void AssertBadRequestResult(
        IResult result,
        string expectedCode,
        string? expectedField = null,
        string? expectedScopeId = null)
    {
        result.GetType().Name.Should().StartWith("BadRequest");

        var statusCodeProp = result.GetType().GetProperty("StatusCode");
        var statusCode = statusCodeProp?.GetValue(result) as int?;
        statusCode.Should().Be(StatusCodes.Status400BadRequest);

        var valueProp = result.GetType().GetProperty("Value");
        var value = valueProp?.GetValue(result);
        value.Should().NotBeNull();

        var codeProp = value!.GetType().GetProperty("code");
        var code = codeProp?.GetValue(value) as string;
        code.Should().Be(expectedCode);

        if (expectedField != null)
        {
            var fieldProp = value.GetType().GetProperty("field");
            var field = fieldProp?.GetValue(value) as string;
            field.Should().Be(expectedField);
        }

        if (expectedScopeId != null)
        {
            var scopeIdProp = value.GetType().GetProperty("scopeId");
            var scopeId = scopeIdProp?.GetValue(value) as string;
            scopeId.Should().Be(expectedScopeId);
        }
    }

    private static void AssertNotFoundResult(IResult result, string expectedCode)
    {
        var statusCodeProp = result.GetType().GetProperty("StatusCode");
        var statusCode = statusCodeProp?.GetValue(result) as int?;
        statusCode.Should().Be(StatusCodes.Status404NotFound);

        var valueProp = result.GetType().GetProperty("Value");
        var value = valueProp?.GetValue(result);
        value.Should().NotBeNull();

        var codeProp = value!.GetType().GetProperty("code");
        var code = codeProp?.GetValue(value) as string;
        code.Should().Be(expectedCode);
    }

    private static async Task<TResult> InvokeHandle<TResult>(string methodName, params object?[] args)
    {
        var method = typeof(StudioMemberEndpoints)
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method {methodName} not found.");
        var task = (Task<IResult>)method.Invoke(null, args)!;
        return (TResult)(object)await task;
    }

    private sealed class RecordingMemberService : IStudioMemberService
    {
        public StudioMemberSummaryResponse? CreateResponse { get; set; }
        public Exception? CreateException { get; set; }
        public bool CreateInvoked { get; private set; }
        public CreateStudioMemberRequest? CreateRequest { get; private set; }

        public StudioMemberRosterResponse? ListResponse { get; set; }
        public StudioMemberDetailResponse? GetResponse { get; set; }
        public Exception? GetException { get; set; }
        public StudioMemberBindingAcceptedResponse? BindResponse { get; set; }
        public Exception? BindException { get; set; }
        public UpdateStudioMemberBindingRequest? BindRequest { get; private set; }
        public StudioMemberBindingContractResponse? GetBindingResponse { get; set; }
        public StudioMemberBindingRunStatusResponse? GetBindingRunResponse { get; set; }
        public Exception? GetBindingRunException { get; set; }
        public StudioMemberEndpointContractResponse? EndpointContractResponse { get; set; }
        public Exception? EndpointContractException { get; set; }
        public StudioMemberBindingActivationResponse? ActivateResponse { get; set; }
        public Exception? ActivateException { get; set; }
        public StudioMemberBindingRevisionActionResponse? RetireResponse { get; set; }
        public Exception? RetireException { get; set; }

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId, CreateStudioMemberRequest request, CancellationToken ct = default)
        {
            CreateInvoked = true;
            CreateRequest = request;
            if (CreateException != null) throw CreateException;
            return Task.FromResult(CreateResponse!);
        }

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default)
            => Task.FromResult(ListResponse ?? new StudioMemberRosterResponse(scopeId, []));

        public Task<StudioMemberDetailResponse> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default)
        {
            if (GetException != null) throw GetException;
            return Task.FromResult(
                GetResponse ?? throw new StudioMemberNotFoundException(scopeId, memberId));
        }

        public Task<StudioMemberBindingAcceptedResponse> BindAsync(
            string scopeId, string memberId, UpdateStudioMemberBindingRequest request, CancellationToken ct = default)
        {
            BindRequest = request;
            if (BindException != null) throw BindException;
            return Task.FromResult(BindResponse!);
        }

        public Task<StudioMemberBindingViewResponse> GetBindingAsync(
            string scopeId, string memberId, CancellationToken ct = default)
            => Task.FromResult(new StudioMemberBindingViewResponse(GetBindingResponse));

        public Task<StudioMemberBindingRunStatusResponse> GetBindingRunAsync(
            string scopeId, string memberId, string bindingRunId, CancellationToken ct = default)
        {
            if (GetBindingRunException != null) throw GetBindingRunException;
            return Task.FromResult(GetBindingRunResponse!);
        }

        public Task<StudioMemberEndpointContractResponse?> GetEndpointContractAsync(
            string scopeId, string memberId, string endpointId, CancellationToken ct = default)
        {
            if (EndpointContractException != null) throw EndpointContractException;
            return Task.FromResult(EndpointContractResponse);
        }

        public Task<StudioMemberBindingActivationResponse> ActivateBindingRevisionAsync(
            string scopeId, string memberId, string revisionId, CancellationToken ct = default)
        {
            if (ActivateException != null) throw ActivateException;
            return Task.FromResult(ActivateResponse!);
        }

        public Task<StudioMemberBindingRevisionActionResponse> RetireBindingRevisionAsync(
            string scopeId, string memberId, string revisionId, CancellationToken ct = default)
        {
            if (RetireException != null) throw RetireException;
            return Task.FromResult(RetireResponse!);
        }

        public Task<StudioMemberCommandResponse> UpdateAsync(
            string scopeId, string memberId, UpdateStudioMemberRequest request, CancellationToken ct = default)
        {
            UpdateInvoked = true;
            UpdateScopeId = scopeId;
            UpdateMemberId = memberId;
            UpdateRequest = request;
            if (UpdateException != null) throw UpdateException;
            return Task.FromResult(UpdateResponse ?? new StudioMemberCommandResponse(
                StudioMemberCommandStatusNames.Accepted,
                scopeId,
                memberId,
                DateTimeOffset.UtcNow));
        }

        public bool UpdateInvoked { get; set; }
        public string? UpdateScopeId { get; set; }
        public string? UpdateMemberId { get; set; }
        public UpdateStudioMemberRequest? UpdateRequest { get; set; }
        public StudioMemberCommandResponse? UpdateResponse { get; set; }
        public Exception? UpdateException { get; set; }

        public bool DeleteInvoked { get; set; }
        public string? DeleteScopeId { get; set; }
        public string? DeleteMemberId { get; set; }
        public Exception? DeleteException { get; set; }

        public Task<StudioMemberCommandResponse> DeleteAsync(
            string scopeId, string memberId, CancellationToken ct = default)
        {
            DeleteInvoked = true;
            DeleteScopeId = scopeId;
            DeleteMemberId = memberId;
            if (DeleteException != null) throw DeleteException;
            return Task.FromResult(new StudioMemberCommandResponse(
                StudioMemberCommandStatusNames.DeleteAccepted,
                scopeId,
                memberId,
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class ProjectionBackedMemberService(
        ProjectionStudioMemberQueryPort queryPort) : IStudioMemberService
    {
        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId,
            CreateStudioMemberRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("delete projection regression must not create members.");

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            queryPort.ListAsync(scopeId, page, ct);

        public async Task<StudioMemberDetailResponse> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            await queryPort.GetAsync(scopeId, memberId, ct)
            ?? throw new StudioMemberNotFoundException(scopeId, memberId);

        public Task<StudioMemberBindingAcceptedResponse> BindAsync(
            string scopeId,
            string memberId,
            UpdateStudioMemberBindingRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("delete projection regression must not bind members.");

        public Task<StudioMemberBindingViewResponse> GetBindingAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("delete projection regression must not read bindings.");

        public Task<StudioMemberBindingRunStatusResponse> GetBindingRunAsync(
            string scopeId,
            string memberId,
            string bindingRunId,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("delete projection regression must not read binding runs.");

        public Task<StudioMemberEndpointContractResponse?> GetEndpointContractAsync(
            string scopeId,
            string memberId,
            string endpointId,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("delete projection regression must not read endpoint contracts.");

        public Task<StudioMemberBindingActivationResponse> ActivateBindingRevisionAsync(
            string scopeId,
            string memberId,
            string revisionId,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("delete projection regression must not activate revisions.");

        public Task<StudioMemberBindingRevisionActionResponse> RetireBindingRevisionAsync(
            string scopeId,
            string memberId,
            string revisionId,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("delete projection regression must not retire revisions.");

        public Task<StudioMemberCommandResponse> UpdateAsync(
            string scopeId,
            string memberId,
            UpdateStudioMemberRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("delete projection regression must not update members.");

        public Task<StudioMemberCommandResponse> DeleteAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberCommandResponse(
                StudioMemberCommandStatusNames.DeleteAccepted,
                scopeId,
                memberId,
                DateTimeOffset.Parse("2026-07-29T00:00:08Z")));
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.Studio.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
