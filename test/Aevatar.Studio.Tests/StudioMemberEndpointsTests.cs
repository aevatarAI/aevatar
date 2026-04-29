using System.Reflection;
using System.Security.Claims;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
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

        result.Should().BeOfType<Created<StudioMemberSummaryResponse>>()
            .Which.Location.Should().Be($"/api/scopes/{ScopeId}/members/{NewSummary().MemberId}");
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
    public async Task HandleBindAsync_ShouldReturnAccepted_OnSuccess()
    {
        var binding = new StudioMemberBindingAcceptedResponse(
            ScopeId: ScopeId,
            MemberId: "m-1",
            BindingId: "bind-1",
            Status: StudioMemberBindingStatusNames.Accepted,
            AcceptedAt: DateTimeOffset.UtcNow);
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
                Workflow: new StudioMemberWorkflowBindingSpec(["w:"])),
            service,
            CancellationToken.None);

        result.Should().BeOfType<Accepted<StudioMemberBindingAcceptedResponse>>()
            .Which.Value.Should().BeSameAs(binding);
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
            GetBindingResponse = new StudioMemberBindingViewResponse(contract),
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
        RevisionId: "rev-1");

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

    private static void AssertBadRequestResult(IResult result, string expectedCode)
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

        public StudioMemberRosterResponse? ListResponse { get; set; }
        public StudioMemberDetailResponse? GetResponse { get; set; }
        public Exception? GetException { get; set; }
        public StudioMemberBindingAcceptedResponse? BindResponse { get; set; }
        public Exception? BindException { get; set; }
        public StudioMemberBindingViewResponse? GetBindingResponse { get; set; }
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
            if (BindException != null) throw BindException;
            return Task.FromResult(BindResponse!);
        }

        public Task<StudioMemberBindingViewResponse> GetBindingAsync(
            string scopeId, string memberId, CancellationToken ct = default)
            => Task.FromResult(GetBindingResponse ?? new StudioMemberBindingViewResponse(null));

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

        public Task<StudioMemberDetailResponse> UpdateAsync(
            string scopeId, string memberId, UpdateStudioMemberRequest request, CancellationToken ct = default)
        {
            UpdateInvoked = true;
            UpdateScopeId = scopeId;
            UpdateMemberId = memberId;
            UpdateRequest = request;
            if (UpdateException != null) throw UpdateException;
            return Task.FromResult(UpdateResponse!);
        }

        public bool UpdateInvoked { get; set; }
        public string? UpdateScopeId { get; set; }
        public string? UpdateMemberId { get; set; }
        public UpdateStudioMemberRequest? UpdateRequest { get; set; }
        public StudioMemberDetailResponse? UpdateResponse { get; set; }
        public Exception? UpdateException { get; set; }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.Studio.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
