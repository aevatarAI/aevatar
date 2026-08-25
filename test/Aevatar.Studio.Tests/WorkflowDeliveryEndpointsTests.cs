using System.Security.Claims;
using System.Text.Json;
using Aevatar.Authentication.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Hosting.Endpoints.Schedules;
using Aevatar.Studio.Application.Delivery;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowDeliveryEndpointsTests
{
    private const string CustomerScopeId = "scope-customer-alpha";
    private const string CustomerUserId = "user-customer-alpha";
    private const string DeliveryId = "delivery-beta";
    private const string TeamId = "team-gamma";

    [Theory]
    [InlineData("list")]
    [InlineData("parse")]
    [InlineData("create")]
    public async Task AdminEndpoints_WhenGrantIsNotAllowedUserId_ShouldForbidWithoutCallingService(
        string operation)
    {
        var service = new RecordingWorkflowDeliveryService();
        var authorizer = new FixedPlatformAdminAuthorizer(new PlatformCaller(
            IsElevated: true,
            Role: "admin",
            Email: "admin@example.test",
            UserId: "admin-not-allowlisted",
            GrantSource: PlatformAdminGrantSources.AllowedEmail));
        var http = CreateContext("scope-admin-view", "user-admin-view");

        var result = operation switch
        {
            "list" => await WorkflowDeliveryEndpoints.ListPackagesAsync(
                http,
                service,
                authorizer,
                CancellationToken.None),
            "parse" => await WorkflowDeliveryEndpoints.ParsePackageAsync(
                http,
                new WorkflowDeliveryPackageRequest("workflow-package-alpha"),
                service,
                authorizer,
                CancellationToken.None),
            "create" => await WorkflowDeliveryEndpoints.CreateRequestAsync(
                http,
                new WorkflowDeliveryCreateRequest(
                    "workflow-create-alpha",
                    "package-version-alpha",
                    "scope-target-delta",
                    "delivery-create-key-alpha"),
                service,
                authorizer,
                CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        result.Should().BeOfType<ForbidHttpResult>();
        service.InvocationCount.Should().Be(0);
        authorizer.ResolveCalls.Should().Be(1);
    }

    [Fact]
    public async Task ListPackages_WhenDeploymentCatalogIsEmpty_ShouldReturnHonestEmptyList()
    {
        var service = new RecordingWorkflowDeliveryService
        {
            PackageListResponse = new WorkflowDeliveryPackageListResponse([]),
        };
        var authorizer = new FixedPlatformAdminAuthorizer(new PlatformCaller(
            IsElevated: true,
            Role: "admin",
            Email: "admin@example.test",
            UserId: "admin-allowed",
            GrantSource: PlatformAdminGrantSources.AllowedUserId));

        var result = await WorkflowDeliveryEndpoints.ListPackagesAsync(
            CreateContext("scope-admin-view", "user-admin-view"),
            service,
            authorizer,
            CancellationToken.None);

        result.Should().BeOfType<Ok<WorkflowDeliveryPackageListResponse>>()
            .Which.Value!.Items.Should().BeEmpty();
        service.InvocationCount.Should().Be(1);
    }

    [Theory]
    [InlineData(true, "admin-allowed-alpha", PlatformAdminGrantSources.AllowedUserId, true)]
    [InlineData(false, "admin-not-elevated", PlatformAdminGrantSources.AllowedUserId, false)]
    [InlineData(true, " ", PlatformAdminGrantSources.AllowedUserId, false)]
    [InlineData(true, "admin-email-grant", PlatformAdminGrantSources.AllowedEmail, false)]
    [InlineData(true, "admin-role-grant", PlatformAdminGrantSources.NyxIdPlatformRole, false)]
    public async Task Session_ShouldDeriveViewerFromUniqueClaimsAndRequireExactAdminGrant(
        bool isElevated,
        string adminUserId,
        string grantSource,
        bool expectedAdmin)
    {
        const string sessionScopeId = "scope-session-epsilon";
        const string sessionUserId = "user-session-zeta";
        var authorizer = new FixedPlatformAdminAuthorizer(new PlatformCaller(
            isElevated,
            "admin",
            "session-admin@example.test",
            adminUserId,
            grantSource));
        var http = CreateContext(sessionScopeId, sessionUserId);

        var result = await WorkflowDeliveryEndpoints.GetSessionAsync(
            http,
            authorizer,
            CancellationToken.None);

        var session = result.Should().BeOfType<Ok<WorkflowDeliverySessionResponse>>()
            .Subject.Value!;
        session.Should().NotBeNull();
        session.ScopeId.Should().Be(sessionScopeId);
        session.Viewer.UserId.Should().Be(sessionUserId);
        session.IsAdmin.Should().Be(expectedAdmin);
        session.CanCreateDeliveries.Should().Be(expectedAdmin);
    }

    [Fact]
    public async Task Publish_WhenRouteScopeDiffersFromUniqueClaim_ShouldForbidWithoutCallingService()
    {
        const string routeScopeId = "scope-route-eta";
        var service = new RecordingWorkflowDeliveryService();
        var bindingQuery = new RecordingIdentityBindingQueryPort();
        var http = CreateContext(CustomerScopeId, CustomerUserId, bindingQuery);

        var result = await WorkflowDeliveryEndpoints.PublishAsync(
            http,
            routeScopeId,
            DeliveryId,
            CreatePublishRequest(),
            service,
            bindingQuery,
            CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        service.InvocationCount.Should().Be(0);
        bindingQuery.ResolveCalls.Should().Be(0);
    }

    [Fact]
    public async Task CreateConnectLink_ShouldForwardBrowserBearerWithoutInventingCallbackAuthority()
    {
        var service = new RecordingWorkflowDeliveryService();
        var http = CreateContext(CustomerScopeId, CustomerUserId);

        var result = await WorkflowDeliveryEndpoints.CreateConnectLinkAsync(
            http,
            CustomerScopeId,
            DeliveryId,
            "lark",
            service,
            CancellationToken.None);

        var accepted = result.Should().BeOfType<Accepted<WorkflowDeliveryConnectLinkResponse>>()
            .Subject;
        accepted.Value!.Status.Should().Be("begin_accepted");
        accepted.Value.ConnectLinkId.Should().Be("link-created");
        accepted.Location.Should().Be(accepted.Value.StatusUrl);
        service.ConnectLinkBearer.Should().Be("delivery-runtime-token");
    }

    [Theory]
    [InlineData("CONNECTIONS_LOCKED")]
    [InlineData("CONNECTION_ALREADY_PENDING")]
    public async Task CreateConnectLink_WhenConnectionMutationConflicts_ShouldReturn409(string code)
    {
        var service = new RecordingWorkflowDeliveryService
        {
            ConnectLinkException = new WorkflowDeliveryException(code, "connection mutation conflict"),
        };
        var http = CreateContext(CustomerScopeId, CustomerUserId);
        http.Response.Body = new MemoryStream();

        var result = await WorkflowDeliveryEndpoints.CreateConnectLinkAsync(
            http,
            CustomerScopeId,
            DeliveryId,
            "lark",
            service,
            CancellationToken.None);
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(http.Response.Body);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        body.RootElement.GetProperty("code").GetString().Should().Be(code);
    }

    [Fact]
    public async Task CreateConnectLink_WhenCommittedProjectionIsNotObserved_ShouldReturnRetryable503()
    {
        var service = new RecordingWorkflowDeliveryService
        {
            ConnectLinkException = new WorkflowDeliveryException(
                "CONNECTION_OBSERVATION_TIMEOUT",
                "connection projection was not observed"),
        };
        var http = CreateContext(CustomerScopeId, CustomerUserId);
        http.Response.Body = new MemoryStream();

        var result = await WorkflowDeliveryEndpoints.CreateConnectLinkAsync(
            http,
            CustomerScopeId,
            DeliveryId,
            "lark",
            service,
            CancellationToken.None);
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(http.Response.Body);

        http.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.RootElement.GetProperty("code").GetString().Should().Be("CONNECTION_OBSERVATION_TIMEOUT");
        body.RootElement.GetProperty("retryable").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("list")]
    [InlineData("attach")]
    public async Task ExistingConnectionEndpoints_WhenRouteScopeDiffers_ShouldForbidBeforeBearerOrBodyValidation(
        string operation)
    {
        var service = new RecordingWorkflowDeliveryService();
        var http = CreateContext(CustomerScopeId, CustomerUserId);
        http.Request.Headers.Remove("Authorization");

        var result = operation switch
        {
            "list" => await WorkflowDeliveryEndpoints.ListExistingConnectionsAsync(
                http,
                "scope-route-other",
                DeliveryId,
                "lark",
                service,
                CancellationToken.None),
            "attach" => await WorkflowDeliveryEndpoints.AttachExistingConnectionAsync(
                http,
                "scope-route-other",
                DeliveryId,
                "lark",
                request: null,
                service,
                CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        service.InvocationCount.Should().Be(0);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("attach")]
    public async Task ExistingConnectionEndpoints_WithoutBearer_ShouldRejectBeforeCallingService(
        string operation)
    {
        var service = new RecordingWorkflowDeliveryService();
        var http = CreateContext(CustomerScopeId, CustomerUserId);
        http.Request.Headers.Remove("Authorization");

        var result = operation switch
        {
            "list" => await WorkflowDeliveryEndpoints.ListExistingConnectionsAsync(
                http,
                CustomerScopeId,
                DeliveryId,
                "lark",
                service,
                CancellationToken.None),
            "attach" => await WorkflowDeliveryEndpoints.AttachExistingConnectionAsync(
                http,
                CustomerScopeId,
                DeliveryId,
                "lark",
                request: null,
                service,
                CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        result.Should().BeOfType<UnauthorizedHttpResult>();
        service.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task AttachExistingConnection_WithBearerButNoBody_ShouldReturn422()
    {
        var service = new RecordingWorkflowDeliveryService();
        var http = CreateContext(CustomerScopeId, CustomerUserId);
        http.Response.Body = new MemoryStream();

        var result = await WorkflowDeliveryEndpoints.AttachExistingConnectionAsync(
            http,
            CustomerScopeId,
            DeliveryId,
            "lark",
            request: null,
            service,
            CancellationToken.None);
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(http.Response.Body);

        http.Response.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        body.RootElement.GetProperty("code").GetString().Should().Be("INVALID_DELIVERY_REQUEST");
        service.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task ExistingConnectionEndpoints_ShouldForwardExactConnectionIdentityAndBearer()
    {
        var service = new RecordingWorkflowDeliveryService();
        var http = CreateContext(CustomerScopeId, CustomerUserId);

        var listResult = await WorkflowDeliveryEndpoints.ListExistingConnectionsAsync(
            http,
            CustomerScopeId,
            DeliveryId,
            "lark",
            service,
            CancellationToken.None);
        var attachResult = await WorkflowDeliveryEndpoints.AttachExistingConnectionAsync(
            http,
            CustomerScopeId,
            DeliveryId,
            "lark",
            new WorkflowDeliveryAttachConnectionRequest("user-service-existing"),
            service,
            CancellationToken.None);

        listResult.Should().BeOfType<Ok<WorkflowDeliveryExistingConnectionListResponse>>()
            .Which.Value.Should().BeSameAs(service.ExistingConnectionsResponse);
        attachResult.Should().BeOfType<Ok<WorkflowDeliveryAttachedConnectionResponse>>()
            .Which.Value.Should().Be(new WorkflowDeliveryAttachedConnectionResponse(
                "lark",
                "completed",
                "user-service-existing"));
        service.ExistingConnectionsCalls.Should().Be(1);
        service.AttachExistingConnectionCalls.Should().Be(1);
        service.ExistingConnectionsBearer.Should().Be("delivery-runtime-token");
        service.AttachedConnectionBearer.Should().Be("delivery-runtime-token");
        service.AttachedDeliveryId.Should().Be(DeliveryId);
        service.AttachedScopeId.Should().Be(CustomerScopeId);
        service.AttachedSlotKey.Should().Be("lark");
        service.AttachedRequest.Should().Be(new WorkflowDeliveryAttachConnectionRequest(
            "user-service-existing"));
    }

    [Theory]
    [InlineData(
        NyxIdUserServiceInventoryFailureKind.AuthenticationRejected,
        StatusCodes.Status401Unauthorized,
        "NYXID_CONNECTION_INVENTORY_AUTHENTICATION_REJECTED")]
    [InlineData(
        NyxIdUserServiceInventoryFailureKind.Forbidden,
        StatusCodes.Status403Forbidden,
        "NYXID_CONNECTION_INVENTORY_FORBIDDEN")]
    [InlineData(
        NyxIdUserServiceInventoryFailureKind.RateLimited,
        StatusCodes.Status429TooManyRequests,
        "NYXID_CONNECTION_INVENTORY_RATE_LIMITED")]
    [InlineData(
        NyxIdUserServiceInventoryFailureKind.ResponseInvalid,
        StatusCodes.Status502BadGateway,
        "NYXID_CONNECTION_INVENTORY_RESPONSE_INVALID")]
    [InlineData(
        NyxIdUserServiceInventoryFailureKind.Unavailable,
        StatusCodes.Status503ServiceUnavailable,
        "NYXID_CONNECTION_INVENTORY_UNAVAILABLE")]
    public async Task ListExistingConnections_WhenInventoryFails_ShouldMapTypedHttpError(
        NyxIdUserServiceInventoryFailureKind failureKind,
        int expectedStatus,
        string expectedCode)
    {
        var service = new RecordingWorkflowDeliveryService
        {
            ExistingConnectionsException = new NyxIdUserServiceInventoryException(
                failureKind,
                "NyxID inventory failed safely."),
        };
        var http = CreateContext(CustomerScopeId, CustomerUserId);
        http.Response.Body = new MemoryStream();

        var result = await WorkflowDeliveryEndpoints.ListExistingConnectionsAsync(
            http,
            CustomerScopeId,
            DeliveryId,
            "lark",
            service,
            CancellationToken.None);
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(http.Response.Body);

        http.Response.StatusCode.Should().Be(expectedStatus);
        body.RootElement.GetProperty("code").GetString().Should().Be(expectedCode);
        body.RootElement.GetProperty("message").GetString().Should()
            .Be("NyxID inventory failed safely.");
    }

    [Fact]
    public async Task GetConnectStatus_ShouldReadProjectedStatusWithoutForwardingBearer()
    {
        var service = new RecordingWorkflowDeliveryService();
        var http = CreateContext(CustomerScopeId, CustomerUserId);
        http.Request.Headers.Remove("Authorization");

        var result = await WorkflowDeliveryEndpoints.GetConnectStatusAsync(
            http,
            CustomerScopeId,
            DeliveryId,
            "lark",
            service,
            CancellationToken.None);

        var response = result.Should().BeOfType<Ok<WorkflowDeliveryConnectStatusResponse>>()
            .Subject.Value!;
        response.Status.Should().Be("pending");
        service.GetConnectionCalls.Should().Be(1);
        service.RefreshConnectionCalls.Should().Be(0);
    }

    [Fact]
    public async Task RefreshConnectStatus_ShouldReturn202WithoutClaimingObservedState()
    {
        var service = new RecordingWorkflowDeliveryService();
        var http = CreateContext(CustomerScopeId, CustomerUserId);

        var result = await WorkflowDeliveryEndpoints.RefreshConnectStatusAsync(
            http,
            CustomerScopeId,
            DeliveryId,
            "lark",
            service,
            CancellationToken.None);

        var accepted = result.Should()
            .BeOfType<Accepted<WorkflowDeliveryConnectionRefreshAcceptedResponse>>()
            .Subject;
        accepted.Value!.Status.Should().Be("refresh_accepted");
        accepted.Location.Should().Be(accepted.Value.StatusUrl);
        service.RefreshConnectionCalls.Should().Be(1);
        service.GetConnectionCalls.Should().Be(0);
    }

    [Fact]
    public async Task Publish_WhenApplicationPersistsAcceptedInstallation_ShouldReturn202WithoutClaimingProvisioning()
    {
        var response = new WorkflowInstallationAcceptedResponse(
            "installation-theta",
            "accepted",
            $"/api/scopes/{CustomerScopeId}/installations/installation-theta",
            "/admin#/studio/team-gamma");
        var service = new RecordingWorkflowDeliveryService { PublishResponse = response };
        var bindingQuery = new RecordingIdentityBindingQueryPort();
        var http = CreateContext(CustomerScopeId, CustomerUserId, bindingQuery);

        var result = await WorkflowDeliveryEndpoints.PublishAsync(
            http,
            CustomerScopeId,
            DeliveryId,
            CreatePublishRequest(),
            service,
            bindingQuery,
            CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var accepted = result.Should().BeOfType<Accepted<WorkflowInstallationAcceptedResponse>>().Subject;
        accepted.Location.Should().Be(response.StatusUrl);
        accepted.Value.Should().BeSameAs(response);
        accepted.Value!.Status.Should().Be("accepted").And.NotBe("provisioning_accepted").And.NotBe("ready");
        service.PublishCalls.Should().Be(1);
        service.PublishedScopeId.Should().Be(CustomerScopeId);
        service.PublishedDeliveryId.Should().Be(DeliveryId);
        service.PublishedCaller!.CallerCredential.ExternalUserId.Should().Be(CustomerUserId);
        service.PublishedCaller.AuthenticatedOwner!.VerifiedBindingId.Should().Be("binding-kappa");
        service.PublishedCaller.AuthenticatedOwner.SubjectPlatform.Should().Be("nyxid");
        service.PublishedCaller.AuthenticatedOwner.SubjectTenant.Should().BeEmpty();
    }

    [Fact]
    public async Task InstallationObservationTimeout_ShouldMapToRetryable503()
    {
        WorkflowDeliveryHttpErrorMapper.TryMap(
                new WorkflowDeliveryException(
                    "INSTALLATION_OBSERVATION_TIMEOUT",
                    "installation projection was not observed"),
                out var result)
            .Should().BeTrue();
        var http = CreateContext(CustomerScopeId, CustomerUserId);
        http.Response.Body = new MemoryStream();

        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(http.Response.Body);

        http.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        body.RootElement.GetProperty("code").GetString().Should()
            .Be("INSTALLATION_OBSERVATION_TIMEOUT");
        body.RootElement.GetProperty("retryable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task BindingRequired_ShouldDirectApiClientsBackToDeliveryLoginFinalization()
    {
        WorkflowDeliveryHttpErrorMapper.TryMap(
                new StudioMemberAutomationAuthorizationBindingRequiredException(),
                out var result)
            .Should().BeTrue();
        var http = CreateContext(CustomerScopeId, CustomerUserId);
        http.Response.Body = new MemoryStream();

        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(http.Response.Body);

        http.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        body.RootElement.GetProperty("code").GetString().Should()
            .Be("DELIVERY_AUTHORIZATION_BINDING_REQUIRED");
        var message = body.RootElement.GetProperty("message").GetString();
        message.Should().Contain("Delivery Center");
        message.Should().NotContain("Aevatar console");
    }

    private static WorkflowDeliveryPublishRequest CreatePublishRequest() =>
        new(
            TeamId,
            "publish-key-iota",
            TriggerIntent: new WorkflowDeliveryTriggerRequest("none"));

    private static HttpContext CreateContext(
        string scopeId,
        string userId,
        IExternalIdentityBindingQueryPort? bindingQuery = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "true",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        if (bindingQuery is not null)
            services.AddSingleton<IExternalIdentityBindingQueryPort>(bindingQuery);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("scope_id", scopeId),
                new Claim("sub", userId),
                new Claim("name", "Delivery Viewer"),
                new Claim("email", "viewer@example.test"),
            ],
            "test")),
            RequestServices = services.BuildServiceProvider(),
        };
        http.Request.Headers.Authorization = "Bearer delivery-runtime-token";
        return http;
    }

    private sealed class FixedPlatformAdminAuthorizer(PlatformCaller caller) : IPlatformAdminAuthorizer
    {
        public int ResolveCalls { get; private set; }

        public Task<PlatformCaller> ResolveCallerAsync(
            string bearerToken,
            CancellationToken ct = default)
        {
            ResolveCalls++;
            bearerToken.Should().Be("delivery-runtime-token");
            return Task.FromResult(caller);
        }
    }

    private sealed class RecordingIdentityBindingQueryPort : IExternalIdentityBindingQueryPort
    {
        public int ResolveCalls { get; private set; }

        public Task<BindingId?> ResolveAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default)
        {
            ResolveCalls++;
            return Task.FromResult<BindingId?>(new BindingId { Value = "binding-kappa" });
        }
    }

    private sealed class RecordingWorkflowDeliveryService : IWorkflowDeliveryService
    {
        public WorkflowInstallationAcceptedResponse PublishResponse { get; init; } =
            new(
                "installation-default",
                "accepted",
                "/api/scopes/scope-default/installations/installation-default",
                null);

        public WorkflowDeliveryConnectStatusResponse ConnectStatusResponse { get; init; } =
            new(
                "lark",
                "pending",
                "link-created",
                null,
                DateTimeOffset.Parse("2026-08-16T14:15:16Z"));

        public WorkflowDeliveryConnectionRefreshAcceptedResponse RefreshConnectionResponse { get; init; } =
            new(
                "lark",
                "refresh_accepted",
                $"/api/scopes/{CustomerScopeId}/delivery-requests/{DeliveryId}/connections/lark");

        public WorkflowDeliveryExistingConnectionListResponse ExistingConnectionsResponse { get; init; } =
            new(
                "lark",
                [new WorkflowDeliveryExistingConnectionView(
                    "user-service-existing",
                    "api-lark-bot",
                    "Existing Lark")]);

        public int InvocationCount { get; private set; }
        public int PublishCalls { get; private set; }
        public int GetConnectionCalls { get; private set; }
        public int RefreshConnectionCalls { get; private set; }
        public int ExistingConnectionsCalls { get; private set; }
        public int AttachExistingConnectionCalls { get; private set; }
        public string? PublishedDeliveryId { get; private set; }
        public string? PublishedScopeId { get; private set; }
        public WorkflowDeliveryCallerContext? PublishedCaller { get; private set; }
        public string? ConnectLinkBearer { get; private set; }
        public string? ExistingConnectionsBearer { get; private set; }
        public string? AttachedConnectionBearer { get; private set; }
        public string? AttachedDeliveryId { get; private set; }
        public string? AttachedScopeId { get; private set; }
        public string? AttachedSlotKey { get; private set; }
        public WorkflowDeliveryAttachConnectionRequest? AttachedRequest { get; private set; }
        public WorkflowDeliveryException? ConnectLinkException { get; init; }
        public NyxIdUserServiceInventoryException? ExistingConnectionsException { get; init; }
        public WorkflowDeliveryPackageListResponse PackageListResponse { get; init; } =
            new([]);

        public Task<WorkflowDeliveryPackageListResponse> ListPackagesAsync(
            string principalId,
            CancellationToken ct = default)
        {
            InvocationCount++;
            return Task.FromResult(PackageListResponse);
        }

        public Task<WorkflowDeliveryPackageView> GetPackageAsync(
            string workflowName,
            string principalId,
            CancellationToken ct = default) =>
            Unexpected<WorkflowDeliveryPackageView>();

        public Task<WorkflowDeliveryAcceptedResponse> CreateAsync(
            string principalId,
            WorkflowDeliveryCreateRequest request,
            CancellationToken ct = default) =>
            Unexpected<WorkflowDeliveryAcceptedResponse>();

        public Task<WorkflowDeliveryListResponse> ListAdminAsync(
            WorkflowDeliveryPageRequest? page = null,
            CancellationToken ct = default) =>
            Unexpected<WorkflowDeliveryListResponse>();

        public Task<WorkflowDeliveryListResponse> ListCustomerAsync(
            string scopeId,
            WorkflowDeliveryPageRequest? page = null,
            CancellationToken ct = default) =>
            Unexpected<WorkflowDeliveryListResponse>();

        public Task<WorkflowDeliveryView?> GetAdminAsync(
            string deliveryId,
            CancellationToken ct = default) =>
            Unexpected<WorkflowDeliveryView?>();

        public Task<WorkflowDeliveryView?> GetCustomerAsync(
            string deliveryId,
            string scopeId,
            CancellationToken ct = default) =>
            Unexpected<WorkflowDeliveryView?>();

        public Task<WorkflowDeliveryView> ValidateAccessAsync(
            string deliveryId,
            string scopeId,
            CancellationToken ct = default) =>
            Unexpected<WorkflowDeliveryView>();

        public Task RevokeAsync(
            string deliveryId,
            string principalId,
            CancellationToken ct = default) =>
            Unexpected();

        public Task<WorkflowDeliveryConnectLinkResponse> CreateConnectLinkAsync(
            string deliveryId,
            string scopeId,
            string slotKey,
            string bearerToken,
            CancellationToken ct = default)
        {
            InvocationCount++;
            ConnectLinkBearer = bearerToken;
            if (ConnectLinkException != null)
                return Task.FromException<WorkflowDeliveryConnectLinkResponse>(ConnectLinkException);
            return Task.FromResult(new WorkflowDeliveryConnectLinkResponse(
                slotKey,
                "begin_accepted",
                "link-created",
                "https://nyx.example/connect/redacted",
                $"/api/scopes/{scopeId}/delivery-requests/{deliveryId}/connections/{slotKey}",
                DateTimeOffset.Parse("2026-08-16T14:15:16Z")));
        }

        public Task<WorkflowDeliveryConnectStatusResponse> GetConnectStatusAsync(
            string deliveryId,
            string scopeId,
            string slotKey,
            CancellationToken ct = default)
        {
            InvocationCount++;
            GetConnectionCalls++;
            return Task.FromResult(ConnectStatusResponse);
        }

        public Task<WorkflowDeliveryExistingConnectionListResponse> ListExistingConnectionsAsync(
            string deliveryId,
            string scopeId,
            string slotKey,
            string bearerToken,
            CancellationToken ct = default)
        {
            InvocationCount++;
            ExistingConnectionsCalls++;
            ExistingConnectionsBearer = bearerToken;
            if (ExistingConnectionsException is not null)
            {
                return Task.FromException<WorkflowDeliveryExistingConnectionListResponse>(
                    ExistingConnectionsException);
            }
            return Task.FromResult(ExistingConnectionsResponse);
        }

        public Task<WorkflowDeliveryAttachedConnectionResponse> AttachExistingConnectionAsync(
            string deliveryId,
            string scopeId,
            string slotKey,
            WorkflowDeliveryAttachConnectionRequest request,
            string bearerToken,
            CancellationToken ct = default)
        {
            InvocationCount++;
            AttachExistingConnectionCalls++;
            AttachedDeliveryId = deliveryId;
            AttachedScopeId = scopeId;
            AttachedSlotKey = slotKey;
            AttachedRequest = request;
            AttachedConnectionBearer = bearerToken;
            return Task.FromResult(new WorkflowDeliveryAttachedConnectionResponse(
                slotKey,
                "completed",
                request.UserServiceId));
        }

        public Task<WorkflowDeliveryConnectionRefreshAcceptedResponse> RefreshConnectStatusAsync(
            string deliveryId,
            string scopeId,
            string slotKey,
            string bearerToken,
            CancellationToken ct = default)
        {
            InvocationCount++;
            RefreshConnectionCalls++;
            bearerToken.Should().Be("delivery-runtime-token");
            return Task.FromResult(RefreshConnectionResponse);
        }

        public Task<WorkflowDeliveryConfigurationValidationResponse> ValidateConfigurationAsync(
            string deliveryId,
            string scopeId,
            WorkflowDeliveryValidateConfigurationRequest request,
            WorkflowCapabilityAdmissionContext capabilityContext,
            CancellationToken ct = default) =>
            Unexpected<WorkflowDeliveryConfigurationValidationResponse>();

        public Task<WorkflowInstallationAcceptedResponse> PublishAsync(
            string deliveryId,
            string scopeId,
            WorkflowDeliveryPublishRequest request,
            WorkflowDeliveryCallerContext caller,
            CancellationToken ct = default)
        {
            InvocationCount++;
            PublishCalls++;
            PublishedDeliveryId = deliveryId;
            PublishedScopeId = scopeId;
            PublishedCaller = caller;
            return Task.FromResult(PublishResponse);
        }

        public Task<WorkflowInstallationView?> GetInstallationAsync(
            string scopeId,
            string installationId,
            CancellationToken ct = default) =>
            Unexpected<WorkflowInstallationView?>();

        public Task<WorkflowInstallationAcceptedResponse> RetryAsync(
            string scopeId,
            string installationId,
            WorkflowDeliveryCallerContext caller,
            CancellationToken ct = default) =>
            Unexpected<WorkflowInstallationAcceptedResponse>();

        private Task<T> Unexpected<T>()
        {
            InvocationCount++;
            return Task.FromException<T>(new InvalidOperationException("Workflow delivery service was not expected to be called."));
        }

        private Task Unexpected()
        {
            InvocationCount++;
            return Task.FromException(new InvalidOperationException("Workflow delivery service was not expected to be called."));
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.Studio.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
