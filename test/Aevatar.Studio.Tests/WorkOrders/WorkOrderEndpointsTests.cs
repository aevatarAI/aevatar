using System.Security.Claims;
using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Tests.WorkOrders;

public sealed class WorkOrderEndpointsTests
{
    private const string ScopeId = "scope-1";

    [Fact]
    public async Task HandleCreateAsync_ShouldReturnAcceptedReceiptForAuthenticatedRequester()
    {
        var service = new RecordingWorkOrderService();
        var request = CreateRequest();

        var result = await WorkOrderEndpoints.HandleCreateAsync(
            CreateAuthenticatedContext("requester-1"),
            ScopeId,
            request,
            service,
            CancellationToken.None);

        var accepted = result.Should().BeOfType<Accepted<WorkOrderAcceptedReceipt>>().Subject;
        accepted.Location.Should().Be($"/api/scopes/{ScopeId}/work-orders/wo-1");
        accepted.Value!.Stage.Should().Be(WorkOrderCommandStageNames.DispatchAccepted);
        service.Requester.Should().Be(new WorkOrderPrincipalContract("requester-1", "user"));
        service.CreateRequest.Should().BeSameAs(request);
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldRejectAuthenticatedIdentityWithoutPrincipalId()
    {
        var service = new RecordingWorkOrderService();
        var context = CreateAuthenticatedContext(requesterId: null);

        var result = await WorkOrderEndpoints.HandleCreateAsync(
            context,
            ScopeId,
            CreateRequest(),
            service,
            CancellationToken.None);

        result.Should().BeOfType<UnauthorizedHttpResult>();
        service.CreateRequest.Should().BeNull();
    }

    [Fact]
    public async Task HandleGetAsync_WhenWorkOrderIdIsMalformed_ShouldReturnBadRequest()
    {
        var service = new RecordingWorkOrderService
        {
            GetException = new ArgumentException(
                "sensitive canonical identity details",
                "workOrderId"),
        };

        var result = await WorkOrderEndpoints.HandleGetAsync(
            CreateAuthenticatedContext("requester-1"),
            ScopeId,
            "malformed:work-order",
            service,
            CancellationToken.None);

        AssertMalformedWorkOrderId(result);
    }

    [Fact]
    public async Task HandleDispatchAsync_WhenWorkOrderIdIsMalformed_ShouldReturnBadRequest()
    {
        var service = new RecordingWorkOrderService
        {
            DispatchException = new ArgumentException(
                "sensitive canonical identity details",
                "workOrderId"),
        };

        var result = await WorkOrderEndpoints.HandleDispatchAsync(
            CreateAuthenticatedContext("requester-1"),
            ScopeId,
            "malformed:work-order",
            new DispatchWorkOrderRequest(ExpectedLifecycleVersion: 3),
            service,
            CancellationToken.None);

        AssertMalformedWorkOrderId(result);
    }

    [Theory]
    [InlineData("scopeId")]
    [InlineData(null)]
    public async Task HandleGetAsync_WhenArgumentExceptionIsNotForWorkOrderId_ShouldRethrow(
        string? parameterName)
    {
        var expected = CreateUnrelatedArgumentException(parameterName);
        var service = new RecordingWorkOrderService { GetException = expected };

        var act = () => WorkOrderEndpoints.HandleGetAsync(
            CreateAuthenticatedContext("requester-1"),
            ScopeId,
            "wo-1",
            service,
            CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.Should().BeSameAs(expected);
    }

    [Theory]
    [InlineData("scopeId")]
    [InlineData(null)]
    public async Task HandleDispatchAsync_WhenArgumentExceptionIsNotForWorkOrderId_ShouldRethrow(
        string? parameterName)
    {
        var expected = CreateUnrelatedArgumentException(parameterName);
        var service = new RecordingWorkOrderService { DispatchException = expected };

        var act = () => WorkOrderEndpoints.HandleDispatchAsync(
            CreateAuthenticatedContext("requester-1"),
            ScopeId,
            "wo-1",
            new DispatchWorkOrderRequest(ExpectedLifecycleVersion: 3),
            service,
            CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>())
            .Which.Should().BeSameAs(expected);
    }

    private static CreateWorkOrderRequest CreateRequest() =>
        new(
            TeamId: "team-1",
            MemberId: "member-1",
            PublishedServiceId: "service-1",
            EndpointId: "chat",
            Intent: "Produce the report",
            DedupKey: "dedup-1",
            Input: new WorkOrderServiceInputContract(
                new WorkOrderChatInputContract("Create it")));

    private static HttpContext CreateAuthenticatedContext(string? requesterId)
    {
        var claims = new List<Claim> { new("scope_id", ScopeId) };
        if (requesterId != null)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, requesterId));

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
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
            RequestServices = services,
        };
    }

    private static void AssertMalformedWorkOrderId(IResult result)
    {
        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var value = result.Should().BeAssignableTo<IValueHttpResult>().Which.Value;
        var payload = JsonSerializer.SerializeToElement(value);
        payload.GetProperty("code").GetString().Should().Be("INVALID_WORK_ORDER_ID");
        payload.GetProperty("message").GetString().Should().Be("The WorkOrder identity is malformed.");
        payload.ToString().Should().NotContain("sensitive canonical identity details");
    }

    private static ArgumentException CreateUnrelatedArgumentException(string? parameterName) =>
        parameterName is null
            ? new ArgumentException("sensitive downstream argument details")
            : new ArgumentException("sensitive downstream argument details", parameterName);

    private sealed class RecordingWorkOrderService : IWorkOrderService
    {
        public CreateWorkOrderRequest? CreateRequest { get; private set; }

        public WorkOrderPrincipalContract? Requester { get; private set; }

        public Exception? GetException { get; init; }

        public Exception? DispatchException { get; init; }

        public Task<WorkOrderAcceptedReceipt> CreateAsync(
            string scopeId,
            CreateWorkOrderRequest request,
            WorkOrderPrincipalContract requester,
            CancellationToken ct = default)
        {
            CreateRequest = request;
            Requester = requester;
            return Task.FromResult(new WorkOrderAcceptedReceipt(
                "wo-1",
                "command-1",
                "correlation-1",
                WorkOrderCommandStageNames.DispatchAccepted));
        }

        public Task<WorkOrderListResponse> ListAsync(string scopeId, WorkOrderQueryRequest query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkOrderCurrentStateResponse> GetAsync(string scopeId, string workOrderId, CancellationToken ct = default) =>
            GetException is null
                ? throw new NotSupportedException()
                : Task.FromException<WorkOrderCurrentStateResponse>(GetException);

        public Task<WorkOrderAcceptedReceipt> ReassignAsync(string scopeId, string workOrderId, ReassignWorkOrderRequest request, WorkOrderPrincipalContract requester, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkOrderAcceptedReceipt> DispatchAsync(string scopeId, string workOrderId, DispatchWorkOrderRequest request, WorkOrderPrincipalContract requester, CancellationToken ct = default) =>
            DispatchException is null
                ? throw new NotSupportedException()
                : Task.FromException<WorkOrderAcceptedReceipt>(DispatchException);

        public Task<WorkOrderAcceptedReceipt> CancelAsync(string scopeId, string workOrderId, CancelWorkOrderRequest request, WorkOrderPrincipalContract requester, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Aevatar.Studio.Tests";

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
