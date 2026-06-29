using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ScheduledDispatchCurrentStateProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldMaterializeServiceKey_WhenIdentityIsComplete()
    {
        var store = new RecordingDocumentStore<ScheduledDispatchDocument>(x => x.Id);
        var projector = new ScheduledDispatchCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-06-18T00:00:00+00:00")));
        var identity = new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "default",
            ServiceId = "svc",
        };

        await projector.ProjectAsync(
            CreateContext("scheduled-dispatch:schedule-1"),
            WrapCommitted(
                CreateServiceInvocationState("schedule-1", identity),
                version: 9,
                eventId: "evt-9",
                observedAt: DateTimeOffset.Parse("2026-06-18T01:00:00+00:00")));

        var document = await store.GetAsync("schedule-1");
        document.Should().NotBeNull();
        document!.ServiceKey.Should().Be(ServiceKeys.Build(identity));
        document.ServiceId.Should().Be("svc");
        document.ServiceEndpointId.Should().Be("chat");
        document.Prompt.Should().Be("run");
        document.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.Generic.ToString());
        document.StateVersion.Should().Be(9);
        document.LastEventId.Should().Be("evt-9");
    }

    [Fact]
    public async Task ProjectAsync_ShouldNotProjectDurableSenderBearerToken()
    {
        var store = new RecordingDocumentStore<ScheduledDispatchDocument>(x => x.Id);
        var projector = new ScheduledDispatchCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-06-18T00:00:00+00:00")));
        var identity = new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "default",
            ServiceId = "svc",
        };
        var state = CreateServiceInvocationState("schedule-secret", identity);
        state.Target.ServiceInvocation.Auth = new ScheduledServiceInvocationAuthState
        {
            DurableSenderBearerToken = "durable-run-key",
        };

        await projector.ProjectAsync(
            CreateContext("scheduled-dispatch:schedule-secret"),
            WrapCommitted(
                state,
                version: 10,
                eventId: "evt-secret",
                observedAt: DateTimeOffset.Parse("2026-06-18T01:15:00+00:00")));

        var document = await store.GetAsync("schedule-secret");
        document.Should().NotBeNull();
        document!.ToByteArray().AsSpan().IndexOf(ByteString.CopyFromUtf8("durable-run-key").ToByteArray()).Should().Be(-1);
        document.ToString().Should().NotContain("durable-run-key");
        document.ServiceKey.Should().Be(ServiceKeys.Build(identity));
        document.StateVersion.Should().Be(10);
    }

    [Theory]
    [InlineData("", "app", "default", "svc")]
    [InlineData("tenant", "", "default", "svc")]
    [InlineData("tenant", "app", "", "svc")]
    [InlineData("tenant", "app", "default", "")]
    public async Task ProjectAsync_ShouldMaterializeDocumentWithoutServiceKey_WhenHistoricalIdentityIsIncomplete(
        string tenantId,
        string appId,
        string serviceNamespace,
        string serviceId)
    {
        var store = new RecordingDocumentStore<ScheduledDispatchDocument>(x => x.Id);
        var projector = new ScheduledDispatchCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-06-18T00:00:00+00:00")));

        var act = async () => await projector.ProjectAsync(
            CreateContext("scheduled-dispatch:historical"),
            WrapCommitted(
                CreateServiceInvocationState(
                    "historical",
                    new ServiceIdentity
                    {
                        TenantId = tenantId,
                        AppId = appId,
                        Namespace = serviceNamespace,
                        ServiceId = serviceId,
                    }),
                version: 11,
                eventId: "evt-historical",
                observedAt: DateTimeOffset.Parse("2026-06-18T01:30:00+00:00")));

        await act.Should().NotThrowAsync();
        var document = await store.GetAsync("historical");
        document.Should().NotBeNull();
        document!.ServiceKey.Should().BeEmpty();
        document.ServiceId.Should().Be(serviceId);
        document.ServiceEndpointId.Should().Be("chat");
        document.StateVersion.Should().Be(11);
        document.LastEventId.Should().Be("evt-historical");
    }

    private static ScheduledDispatchProjectionContext CreateContext(string rootActorId) =>
        new()
        {
            RootActorId = rootActorId,
            ProjectionKind = "scheduled-dispatch",
        };

    private static ScheduledDispatchState CreateServiceInvocationState(
        string scheduleId,
        ServiceIdentity identity) =>
        new()
        {
            ScheduleId = scheduleId,
            DisplayName = "Scheduled service invocation",
            CronExpression = "0 9 * * *",
            Timezone = "UTC",
            Enabled = true,
            Target = new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                ServiceInvocation = new ScheduledServiceInvocationTargetState
                {
                    Identity = identity.Clone(),
                    EndpointId = "chat",
                    Payload = Any.Pack(new ChatRequestEvent { Prompt = "run" }),
                },
            },
        };

    private static EventEnvelope WrapCommitted(
        ScheduledDispatchState state,
        long version,
        string eventId,
        DateTimeOffset observedAt) =>
        new()
        {
            Id = $"outer-{eventId}",
            Timestamp = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(1)),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = Timestamp.FromDateTimeOffset(observedAt),
                    EventData = Any.Pack(new ScheduledDispatchConfiguredEvent()),
                },
                StateRoot = Any.Pack(state),
            }),
        };
}
