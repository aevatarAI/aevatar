using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Application.Schedules;
using Aevatar.GAgentService.Infrastructure.Schedules;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScheduledDispatchServiceInvocationTests
{
    [Fact]
    public async Task PrepareAsync_ShouldBuildServiceInvocationAdapterEnvelope()
    {
        var service = new ScheduledDispatchTargetPreparationService();
        var payload = Any.Pack(new StringValue { Value = "run" });
        var configuration = new ScheduledDispatchConfiguration(
            "schedule-1",
            "Daily",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity
                    {
                        TenantId = "tenant",
                        AppId = "app",
                        Namespace = "default",
                        ServiceId = "svc",
                    },
                    "run",
                    payload,
                    "rev-1")),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>());

        var prepared = await service.PrepareAsync(configuration, "cmd-1", "corr-1");

        prepared.TargetActorId.Should().Be(ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId);
        prepared.Descriptor.Kind.Should().Be(ScheduledDispatchTargetKind.ServiceInvocation);
        prepared.PayloadTypeUrl.Should().Be(Any.Pack(new ServiceInvocationRequest()).TypeUrl);
        prepared.TriggerEnvelope.Route.GetTargetActorId()
            .Should().Be(ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId);
        prepared.TriggerEnvelope.Propagation!.CorrelationId.Should().Be("corr-1");
        var request = prepared.TriggerEnvelope.Payload.Unpack<ServiceInvocationRequest>();
        request.Identity.ServiceId.Should().Be("svc");
        request.EndpointId.Should().Be("run");
        request.RevisionId.Should().Be("rev-1");
        request.CommandId.Should().Be("cmd-1");
        request.CorrelationId.Should().Be("corr-1");
        request.Payload.Unpack<StringValue>().Value.Should().Be("run");
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_ShouldInvokeExplicitServiceInvocationPort()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort);

        var receipt = await port.DispatchAsync(
            new ServiceInvocationRequest
            {
                CommandId = "cmd-invoke",
                CorrelationId = "corr-invoke",
                Payload = Any.Pack(new StringValue { Value = "invoke" }),
            });

        invocationPort.Requests.Should().ContainSingle()
            .Which.Payload.Unpack<StringValue>().Value.Should().Be("invoke");
        receipt.Accepted.Should().BeTrue();
        receipt.CommandId.Should().Be("cmd-invoke");
        receipt.CorrelationId.Should().Be("corr-invoke");
        receipt.TargetActorId.Should().Be("service-actor");
    }

    [Fact]
    public void ScheduledServiceInvocationDispatchPort_ShouldNotImplementActorDispatchPort()
    {
        typeof(ScheduledServiceInvocationDispatchPort)
            .Should()
            .NotBeAssignableTo<IActorDispatchPort>();
    }

    private sealed class RecordingServiceInvocationPort : IServiceInvocationPort
    {
        public List<ServiceInvocationRequest> Requests { get; } = [];

        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(
            ServiceInvocationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.Clone());
            return Task.FromResult(new ServiceInvocationAcceptedReceipt
            {
                CommandId = request.CommandId,
                CorrelationId = request.CorrelationId,
                TargetActorId = "service-actor",
            });
        }
    }
}
