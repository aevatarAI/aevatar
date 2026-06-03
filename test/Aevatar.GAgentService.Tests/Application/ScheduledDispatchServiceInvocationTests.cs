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
    public async Task DispatchAdapter_ShouldForwardOnlyServiceInvocationAdapterTarget()
    {
        var inner = new RecordingDispatchPort();
        var invocationPort = new RecordingServiceInvocationPort();
        var resolverCallCount = 0;
        var adapter = new ScheduledServiceInvocationDispatchAdapterPort(
            inner,
            () =>
            {
                resolverCallCount++;
                return invocationPort;
            });

        await adapter.DispatchAsync(
            "regular-actor",
            new EventEnvelope
            {
                Id = "cmd-regular",
                Payload = Any.Pack(new StringValue { Value = "regular" }),
            });

        resolverCallCount.Should().Be(0);
        inner.Calls.Should().ContainSingle().Which.ActorId.Should().Be("regular-actor");

        var admission = await adapter.DispatchAsync(
            ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
            new EventEnvelope
            {
                Id = "cmd-invoke",
                Payload = Any.Pack(new ServiceInvocationRequest
                {
                    CommandId = "cmd-invoke",
                    CorrelationId = "corr-invoke",
                    Payload = Any.Pack(new StringValue { Value = "invoke" }),
                }),
            });

        resolverCallCount.Should().Be(1);
        invocationPort.Requests.Should().ContainSingle()
            .Which.Payload.Unpack<StringValue>().Value.Should().Be("invoke");
        inner.Calls.Should().ContainSingle();
        admission.Accepted.Should().BeTrue();
        admission.CommandId.Should().Be("cmd-invoke");
        admission.CorrelationId.Should().Be("corr-invoke");
        admission.ActorId.Should().Be("service-actor");
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((actorId, envelope.Clone()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
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
