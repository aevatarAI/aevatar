using System.Text.Json;
using Aevatar.AI.ToolProviders.ServiceInvoke.Tools;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.ServiceInvoke.Tests;

public class InvokeServiceToolTests
{
    private static ServiceInvokeOptions DefaultOptions() => new()
    {
        TenantId = "t1",
        AppId = "a1",
        Namespace = "ns1",
    };

    private static StubCatalogReader DefaultCatalogReader() => new(
    [
        new ServiceCatalogSnapshot(
            "t1:a1:ns1:svc1", "t1", "a1", "ns1", "svc1", "Svc1",
            "rev1", "rev1", "dep1", "actor1", "ACTIVE",
            [new ServiceEndpointSnapshot("cmd1", "Submit", "COMMAND", "", "", "")],
            [], DateTimeOffset.UtcNow),
    ]);

    [Fact]
    public async Task ExecuteAsync_ReturnsAcceptedReceipt()
    {
        var port = new StubInvocationPort(new ServiceInvocationAcceptedReceipt
        {
            RequestId = "req1",
            ServiceKey = "t1:a1:ns1:svc1",
            DeploymentId = "dep1",
            TargetActorId = "actor1",
            EndpointId = "cmd1",
            CommandId = "cmd-id",
            CorrelationId = "corr-id",
        });

        var tool = new InvokeServiceTool(port, DefaultCatalogReader(), DefaultOptions());
        var result = await tool.ExecuteAsync("""{"service_id":"svc1","endpoint_id":"cmd1","payload":{"name":"test"}}""");

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
        doc.RootElement.GetProperty("service_key").GetString().Should().Be("t1:a1:ns1:svc1");
    }

    [Fact]
    public async Task ExecuteAsync_RequiresServiceId()
    {
        var port = new StubInvocationPort();
        var tool = new InvokeServiceTool(port, DefaultCatalogReader(), DefaultOptions());
        var result = await tool.ExecuteAsync("""{"endpoint_id":"cmd1"}""");

        result.Should().Contain("service_id");
        result.Should().Contain("required");
    }

    [Fact]
    public async Task ExecuteAsync_RequiresEndpointId()
    {
        var port = new StubInvocationPort();
        var tool = new InvokeServiceTool(port, DefaultCatalogReader(), DefaultOptions());
        var result = await tool.ExecuteAsync("""{"service_id":"svc1"}""");

        result.Should().Contain("endpoint_id");
        result.Should().Contain("required");
    }

    [Fact]
    public async Task ExecuteAsync_HandlesInvocationError()
    {
        var port = new StubInvocationPort(error: "Service 'svc1' was not found.");
        var tool = new InvokeServiceTool(port, DefaultCatalogReader(), DefaultOptions());
        var result = await tool.ExecuteAsync("""{"service_id":"svc1","endpoint_id":"cmd1"}""");

        result.Should().Contain("not found");
    }

    [Fact]
    public async Task ExecuteAsync_HandlesEmptyPayload()
    {
        var port = new StubInvocationPort(new ServiceInvocationAcceptedReceipt
        {
            RequestId = "req1",
            ServiceKey = "k",
            EndpointId = "ep",
            CommandId = "c",
            CorrelationId = "c",
        });

        var tool = new InvokeServiceTool(port, DefaultCatalogReader(), DefaultOptions());
        var result = await tool.ExecuteAsync("""{"service_id":"svc1","endpoint_id":"ep"}""");

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("accepted");
    }

    [Fact]
    public async Task ExecuteAsync_BuildsCorrectIdentity()
    {
        ServiceInvocationRequest? captured = null;
        var port = new StubInvocationPort(captureRequest: r => captured = r);

        var tool = new InvokeServiceTool(port, DefaultCatalogReader(), DefaultOptions());
        await tool.ExecuteAsync("""{"service_id":"svc1","endpoint_id":"cmd1","payload":{"x":1}}""");

        captured.Should().NotBeNull();
        captured!.Identity.TenantId.Should().Be("t1");
        captured.Identity.AppId.Should().Be("a1");
        captured.Identity.Namespace.Should().Be("ns1");
        captured.Identity.ServiceId.Should().Be("svc1");
        captured.EndpointId.Should().Be("cmd1");
        captured.Payload.Should().NotBeNull();
    }

    [Fact]
    public void ApprovalMode_ShouldAlwaysRequire()
    {
        var tool = new InvokeServiceTool(new StubInvocationPort(), DefaultCatalogReader(), DefaultOptions());
        tool.ApprovalMode.Should().Be(Aevatar.AI.Abstractions.ToolProviders.ToolApprovalMode.AlwaysRequire);
    }

    private sealed class StubCatalogReader(IReadOnlyList<ServiceCatalogSnapshot> snapshots) : IServiceCatalogQueryReader
    {
        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(snapshots.FirstOrDefault(s =>
                s.TenantId == identity.TenantId &&
                s.AppId == identity.AppId &&
                s.Namespace == identity.Namespace &&
                s.ServiceId == identity.ServiceId));

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(int take = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>(snapshots.Take(take).ToList());

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>(snapshots
                .Where(s => s.TenantId == tenantId && s.AppId == appId && s.Namespace == @namespace)
                .Take(take).ToList());
    }

    private sealed class StubInvocationPort : IServiceInvocationPort
    {
        private readonly ServiceInvocationAcceptedReceipt? _receipt;
        private readonly string? _error;
        private readonly Action<ServiceInvocationRequest>? _captureRequest;

        public StubInvocationPort(
            ServiceInvocationAcceptedReceipt? receipt = null,
            string? error = null,
            Action<ServiceInvocationRequest>? captureRequest = null)
        {
            _receipt = receipt;
            _error = error;
            _captureRequest = captureRequest;
        }

        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(ServiceInvocationRequest request, CancellationToken ct = default)
        {
            _captureRequest?.Invoke(request);

            if (_error != null)
                throw new InvalidOperationException(_error);

            return Task.FromResult(_receipt ?? new ServiceInvocationAcceptedReceipt
            {
                RequestId = "stub",
                ServiceKey = "stub",
                EndpointId = request.EndpointId,
                CommandId = request.CommandId,
                CorrelationId = request.CorrelationId,
            });
        }
    }
}
