using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
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
    public async Task ProjectAsync_ShouldMaterializePromptFromTriggerEnvelope_WhenTargetPayloadIsMissing()
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
        var state = CreateServiceInvocationState("schedule-envelope", identity);
        state.Target.ServiceInvocation.Payload = null;
        state.TriggerEnvelope = new EventEnvelope
        {
            Payload = Any.Pack(new ServiceInvocationRequest
            {
                Identity = identity.Clone(),
                EndpointId = "chat",
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "from envelope" }),
            }),
        };

        await projector.ProjectAsync(
            CreateContext("scheduled-dispatch:schedule-envelope"),
            WrapCommitted(
                state,
                version: 10,
                eventId: "evt-envelope",
                observedAt: DateTimeOffset.Parse("2026-06-18T01:15:00+00:00")));

        var document = await store.GetAsync("schedule-envelope");
        document.Should().NotBeNull();
        document!.Prompt.Should().Be("from envelope");
        document.StateVersion.Should().Be(10);
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
        document.CredentialSourceKind.Should()
            .Be(ScheduledDispatchCredentialSourceKind.LegacyDurableSenderBearer.ToString());
        document.ServiceKey.Should().Be(ServiceKeys.Build(identity));
        document.StateVersion.Should().Be(10);
    }

    [Fact]
<<<<<<< HEAD
    public async Task ProjectAsync_ShouldMaterializeCredentialRequirementFacts()
=======
    public async Task ProjectAsync_ShouldNotProjectDurableCredentialReference()
>>>>>>> origin/feat/2026-07-10_scheduled-agent-key-credential
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
<<<<<<< HEAD
        var state = CreateServiceInvocationState("schedule-workflow", identity);
        state.ScheduleKind = ScheduledDispatchScheduleKindState.Workflow;
        state.Target.ServiceInvocation.Auth = new ScheduledServiceInvocationAuthState
        {
            SenderNyxId = new ScheduledServiceInvocationNyxIdCredentialSourceState
            {
                Subject = new ScheduledServiceInvocationNyxIdSubjectRefState
                {
                    Platform = "nyxid",
                    ExternalUserId = "owner-1",
                },
                Scope = "proxy",
=======
        var state = CreateServiceInvocationState("schedule-durable-reference", identity);
        state.Target.ServiceInvocation.Auth = new ScheduledServiceInvocationAuthState
        {
            Durable = new ScheduledServiceInvocationDurableCredentialReferenceState
            {
                CredentialId = "credential-projector-1",
                SecretReference = new SecretReference
                {
                    Ref = "sec-projector-1",
                    Purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
                    OwnerScopeKey = "owner-scope-projector",
                    Fingerprint = "fp-projector",
                },
>>>>>>> origin/feat/2026-07-10_scheduled-agent-key-credential
            },
        };

        await projector.ProjectAsync(
<<<<<<< HEAD
            CreateContext("scheduled-dispatch:schedule-workflow"),
            WrapCommitted(
                state,
                version: 13,
                eventId: "evt-credential",
                observedAt: DateTimeOffset.Parse("2026-06-18T01:45:00+00:00")));

        var document = await store.GetAsync("schedule-workflow");
        document.Should().NotBeNull();
        document!.CredentialRequirementTargetKind.Should()
            .Be(ScheduledDispatchCredentialRequirementTargetKind.WorkflowService.ToString());
        document.CredentialSourceKind.Should().Be(ScheduledDispatchCredentialSourceKind.SenderNyxId.ToString());
        document.StateVersion.Should().Be(13);
=======
            CreateContext("scheduled-dispatch:schedule-durable-reference"),
            WrapCommitted(
                state,
                version: 11,
                eventId: "evt-durable-reference",
                observedAt: DateTimeOffset.Parse("2026-06-18T01:20:00+00:00")));

        var document = await store.GetAsync("schedule-durable-reference");
        document.Should().NotBeNull();
        AssertDocumentDoesNotContain(document!, "credential-projector-1");
        AssertDocumentDoesNotContain(document!, "sec-projector-1");
        AssertDocumentDoesNotContain(document!, "owner-scope-projector");
        AssertDocumentDoesNotContain(document!, "fp-projector");
        AssertDocumentDoesNotContain(document!, "resolved-full-key");
        document!.ServiceKey.Should().Be(ServiceKeys.Build(identity));
        document.StateVersion.Should().Be(11);
    }

    [Fact]
    public async Task ProjectAsync_ShouldNotProjectScheduledInvocationAgentKeySecretReference()
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
        var state = CreateServiceInvocationState("schedule-reference", identity);
        state.Target.ServiceInvocation.Auth = new ScheduledServiceInvocationAuthState
        {
            ScheduledInvocationAgentKey = new ScheduledInvocationAgentKeyCredentialReferenceState
            {
                SecretReference = new SecretReference
                {
                    Ref = "sec-sensitive-reference",
                    Purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
                    OwnerScopeKey = "owner-scope-key",
                    Fingerprint = "sha256:sensitive-fingerprint",
                    Version = 1,
                    ExpiresAtUnixMs = DateTimeOffset.Parse("2026-07-18T00:00:00+00:00").ToUnixTimeMilliseconds(),
                },
                ApiKeyId = "api-key-sensitive-id",
                KeyExpiresAtUnixMs = DateTimeOffset.Parse("2026-07-18T00:00:00+00:00").ToUnixTimeMilliseconds(),
            },
        };

        await projector.ProjectAsync(
            CreateContext("scheduled-dispatch:schedule-reference"),
            WrapCommitted(
                state,
                version: 10,
                eventId: "evt-reference",
                observedAt: DateTimeOffset.Parse("2026-06-18T01:15:00+00:00")));

        var document = await store.GetAsync("schedule-reference");
        document.Should().NotBeNull();
        AssertDocumentDoesNotContain(document!, "sec-sensitive-reference");
        AssertDocumentDoesNotContain(document!, "api-key-sensitive-id");
        AssertDocumentDoesNotContain(document!, "sensitive-fingerprint");
        document.StateVersion.Should().Be(10);
>>>>>>> origin/feat/2026-07-10_scheduled-agent-key-credential
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

    [Fact]
    public async Task ProjectAsync_ShouldMirrorOverdueFireDetection()
    {
        var store = new RecordingDocumentStore<ScheduledDispatchDocument>(x => x.Id);
        var projector = new ScheduledDispatchCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-02T00:00:00+00:00")));
        var identity = new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "default",
            ServiceId = "svc",
        };
        var state = CreateServiceInvocationState("schedule-overdue", identity);
        var lastOverdueFireAt = DateTimeOffset.Parse("2026-07-01T09:00:00+00:00");
        state.OverdueFireDetectedCount = 3;
        state.LastOverdueFireAt = lastOverdueFireAt;

        await projector.ProjectAsync(
            CreateContext("scheduled-dispatch:schedule-overdue"),
            WrapCommitted(
                state,
                version: 12,
                eventId: "evt-overdue",
                observedAt: DateTimeOffset.Parse("2026-07-02T01:00:00+00:00")));

        var document = await store.GetAsync("schedule-overdue");
        document.Should().NotBeNull();
        document!.OverdueFireDetectedCount.Should().Be(3);
        document.LastOverdueFireAt.Should().Be(lastOverdueFireAt);
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

    private static void AssertDocumentDoesNotContain(ScheduledDispatchDocument document, string value)
    {
        document.ToByteArray().AsSpan().IndexOf(ByteString.CopyFromUtf8(value).ToByteArray()).Should().Be(-1);
        document.ToString().Should().NotContain(value);
    }
}
