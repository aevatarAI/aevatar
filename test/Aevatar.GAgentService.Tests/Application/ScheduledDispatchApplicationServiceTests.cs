using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Application.Schedules;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.GAgentService.Infrastructure.Schedules;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScheduledDispatchApplicationServiceTests
{
    [Fact]
    public async Task CreateAsync_ShouldNormalizeGeneratedEnvelopeScheduleAndDispatchCreate()
    {
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var queryPort = new RecordingScheduledDispatchQueryPort();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            queryPort,
            new ScheduledDispatchTargetPreparationService(), new NoopScheduledDispatchCredentialAdmissionPort());
        var envelope = new EventEnvelope
        {
            Payload = Any.Pack(new StringValue { Value = "run" }),
            Route = EnvelopeRouteSemantics.CreateDirect("publisher-1", "target-1"),
        };

        var receipt = await service.CreateAsync(new ScheduledDispatchConfiguration(
            string.Empty,
            " Daily ",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.Envelope,
                Envelope: envelope),
            " 0 9 * * * ",
            " UTC ",
            true,
            new Dictionary<string, string>
            {
                [" trace "] = " enabled ",
                [" "] = "ignored",
                ["empty"] = " ",
            }));

        receipt.Accepted.Should().BeTrue();
        receipt.ScheduleId.Should().NotBeNullOrWhiteSpace();
        actorPort.EnsuredScheduleIds.Should().ContainSingle().Which.Should().Be(receipt.ScheduleId);
        var created = actorPort.Created.Should().ContainSingle().Which;
        created.Configuration.ScheduleId.Should().Be(receipt.ScheduleId);
        created.Configuration.DisplayName.Should().Be("Daily");
        created.Configuration.Target.ActorId.Should().Be("target-1");
        created.Configuration.Headers.Should().Contain("trace", "enabled");
        created.Configuration.Headers.Should().NotContainKey("empty");
        created.Dispatch.TargetActorId.Should().Be("target-1");
        created.Dispatch.TriggerEnvelope.Id.Should().Be($"schedule-{receipt.ScheduleId}-trigger");
        created.Dispatch.TriggerEnvelope.Propagation!.CorrelationId.Should().Be($"schedule-{receipt.ScheduleId}");
    }

    [Fact]
    public async Task CreateAsync_ShouldCheckActorExistenceWithoutQueryingReadModel()
    {
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var queryPort = new RecordingScheduledDispatchQueryPort();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            queryPort,
            new ScheduledDispatchTargetPreparationService(), new NoopScheduledDispatchCredentialAdmissionPort());

        await service.CreateAsync(CreateEnvelopeConfiguration("schedule-1"));

        actorPort.ResolvedScheduleIds.Should().ContainSingle().Which.Should().Be("schedule-1");
        actorPort.Created.Should().ContainSingle();
        queryPort.GetScheduleIds.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_WhenActorAlreadyExists_ShouldThrowConflictWithoutPreparingTarget()
    {
        var actorPort = new RecordingScheduledDispatchActorPort();
        actorPort.ExistingScheduleIds.Add("schedule-1");
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(), new NoopScheduledDispatchCredentialAdmissionPort());

        var act = () => service.CreateAsync(CreateEnvelopeConfiguration("schedule-1"));

        await act.Should().ThrowAsync<ScheduledDispatchConflictException>()
            .WithMessage("*already exists*");
        actorPort.EnsuredScheduleIds.Should().BeEmpty();
        actorPort.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_ShouldNormalizeOneShotScheduleAndDispatchCreate()
    {
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(), new NoopScheduledDispatchCredentialAdmissionPort());
        var fireAt = DateTimeOffset.UtcNow.AddHours(1).ToOffset(TimeSpan.FromHours(8));

        await service.CreateAsync(new ScheduledDispatchConfiguration(
            "one-shot-1",
            " Reminder ",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.Envelope,
                ActorId: "actor-1",
                Envelope: new EventEnvelope { Payload = Any.Pack(new Empty()) }),
            "0 9 * * *",
            " Asia/Shanghai ",
            true,
            new Dictionary<string, string>(),
            ScheduleMode: ScheduledDispatchScheduleMode.OneShotAtUtc,
            OneShotFireAt: fireAt));

        var created = actorPort.Created.Should().ContainSingle().Which;
        created.Configuration.ScheduleMode.Should().Be(ScheduledDispatchScheduleMode.OneShotAtUtc);
        created.Configuration.CronExpression.Should().BeEmpty();
        created.Configuration.Timezone.Should().Be("Asia/Shanghai");
        created.Configuration.OneShotFireAt.Should().Be(fireAt.ToUniversalTime());
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectMissingOrPastOneShotFireTime()
    {
        var service = CreateService();
        var missingFireAt = () => service.CreateAsync(CreateEnvelopeConfiguration("one-shot-missing") with
        {
            ScheduleMode = ScheduledDispatchScheduleMode.OneShotAtUtc,
            OneShotFireAt = null,
        });
        var pastFireAt = () => service.CreateAsync(CreateEnvelopeConfiguration("one-shot-past") with
        {
            ScheduleMode = ScheduledDispatchScheduleMode.OneShotAtUtc,
            OneShotFireAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });

        await missingFireAt.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*One-shot fire time is required*");
        await pastFireAt.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*One-shot fire time must be in the future*");
    }

    [Fact]
    public async Task CreateAsync_ShouldPreserveServiceInvocationAuthInActorCommand()
    {
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(), new NoopScheduledDispatchCredentialAdmissionPort());
        var auth = new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
            new ScheduledServiceInvocationNyxIdSubjectRef("lark", "tenant-1", "ou-user-1"),
            "proxy"));

        await service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-auth",
            "Invoke",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    "run",
                    Any.Pack(new StringValue { Value = "invoke" }),
                    Auth: auth)),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()));

        var created = actorPort.Created.Should().ContainSingle().Which;
        created.Configuration.Target.ServiceInvocation!.Auth.Should().BeEquivalentTo(auth);
        created.Dispatch.Descriptor.ServiceInvocation!.Auth!.SenderNyxId!.Subject.ExternalUserId.Should().Be("ou-user-1");
    }

    [Fact]
    public async Task CreateAsync_ShouldNormalizeDurableCredentialReferenceAuth()
    {
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(), new NoopScheduledDispatchCredentialAdmissionPort());

        await service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-durable-auth",
            "Invoke",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    "run",
                    Any.Pack(new StringValue { Value = "invoke" }),
                    Auth: new ScheduledServiceInvocationAuth(CreateDurableCredentialReference(
                            " credential-1 ",
                            " sec-1 ",
                            " owner-scope-1 ")))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()));

        var created = actorPort.Created.Should().ContainSingle().Which;
        var auth = created.Configuration.Target.ServiceInvocation!.Auth!.Durable;
        auth.Should().NotBeNull();
        auth!.CredentialId.Should().Be("credential-1");
        auth.SecretReference.Ref.Should().Be("sec-1");
        auth.SecretReference.Purpose.Should().Be(CredentialSecretPurposes.ScheduledNyxApiKey);
        auth.SecretReference.OwnerScopeKey.Should().Be("owner-scope-1");
        var stateAuth = created.Dispatch.Descriptor.ServiceInvocation!.Auth!.Durable;
        stateAuth.Should().BeEquivalentTo(auth);
    }

    [Theory]
    [InlineData("", "sec-1", "owner-scope-1", "*CredentialId is required*")]
    [InlineData("credential-1", "", "owner-scope-1", "*Ref is required*")]
    [InlineData("credential-1", "sec-1", "", "*OwnerScopeKey is required*")]
    public async Task CreateAsync_ShouldRejectIncompleteDurableCredentialReferenceAuth(
        string credentialId,
        string secretRef,
        string ownerScopeKey,
        string expectedMessage)
    {
        var service = CreateService();

        var act = () => service.CreateAsync(CreateServiceInvocationConfiguration(
            "schedule-durable-auth",
            new ScheduledServiceInvocationAuth(CreateDurableCredentialReference(
                    credentialId,
                    secretRef,
                    ownerScopeKey))));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage(expectedMessage);
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectDurableCredentialReferenceAuthWithoutSecretReference()
    {
        var service = CreateService();

        var act = () => service.CreateAsync(CreateServiceInvocationConfiguration(
            "schedule-durable-auth",
            new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationDurableCredentialReference(
                "credential-1",
                null!))));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*secret reference is required*");
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectDurableCredentialReferenceAuthWithWrongPurpose()
    {
        var service = CreateService();

        var act = () => service.CreateAsync(CreateServiceInvocationConfiguration(
            "schedule-durable-auth",
            new ScheduledServiceInvocationAuth(CreateDurableCredentialReference(purpose: "other-purpose"))));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage($"*{CredentialSecretPurposes.ScheduledNyxApiKey}*");
    }

    [Fact]
    public async Task CreateAsync_ShouldAcceptScheduledInvocationAgentKeyReference()
    {
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort());
        var reference = CreateScheduledInvocationAgentKeyReference();

        await service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-agent-key-auth",
            "Invoke",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    "run",
                    Any.Pack(new StringValue { Value = "invoke" }),
                    Auth: new ScheduledServiceInvocationAuth(ScheduledInvocationAgentKey: reference))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()));

        var auth = actorPort.Created.Should().ContainSingle().Which.Configuration.Target.ServiceInvocation!.Auth;
        auth.Should().NotBeNull();
        auth!.ScheduledInvocationAgentKey.Should().NotBeNull();
        auth.ScheduledInvocationAgentKey!.SecretReference.Purpose.Should().Be(CredentialSecretPurposes.ScheduledInvocationAgentKey);
        auth.ScheduledInvocationAgentKey.ApiKeyId.Should().Be("key-schedule");
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectScheduledInvocationAgentKeyReferenceWithWrongPurpose()
    {
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort());
        var reference = CreateScheduledInvocationAgentKeyReference(CredentialSecretPurposes.ScheduledNyxApiKey);

        var act = () => service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-agent-key-auth",
            "Invoke",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    "run",
                    Any.Pack(new StringValue { Value = "invoke" }),
                    Auth: new ScheduledServiceInvocationAuth(ScheduledInvocationAgentKey: reference))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*scheduled.invocation-agent-key*");
        actorPort.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectWorkflowServiceInvocationWithoutCredentialSource()
    {
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort());

        var act = () => service.CreateAsync(CreateServiceInvocationConfiguration(
            "schedule-workflow-no-auth",
            ScheduledDispatchScheduleKind.Workflow,
            ScheduledDispatchCredentialRequirementTargetKind.WorkflowService));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*requires a typed service invocation credential source*");
        actorPort.Created.Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(ScheduleMutationKind.Ensure))]
    [InlineData(nameof(ScheduleMutationKind.Update))]
    public async Task ExistingWorkflowServiceMutation_WhenAuthIsOmitted_ShouldAdmitPersistedCredentialSource(
        string mutationName)
    {
        var mutation = System.Enum.Parse<ScheduleMutationKind>(mutationName);
        var actorPort = new RecordingScheduledDispatchActorPort();
        var queryPort = new RecordingScheduledDispatchQueryPort
        {
            Detail = CreateSummaryDetail(
                "schedule-workflow-existing-auth",
                ScheduledDispatchTargetKind.ServiceInvocation,
                ScheduledDispatchScheduleKind.Workflow,
                ScheduledDispatchCredentialRequirementTargetKind.WorkflowService,
                ScheduledDispatchCredentialSourceKind.ScopeOwnerNyxId),
        };
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            queryPort,
            new ScheduledDispatchTargetPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort());
        var configuration = CreateServiceInvocationConfiguration(
            "schedule-workflow-existing-auth",
            ScheduledDispatchScheduleKind.Workflow,
            ScheduledDispatchCredentialRequirementTargetKind.WorkflowService);

        if (mutation == ScheduleMutationKind.Ensure)
            await service.EnsureAsync(configuration);
        else
            await service.UpdateAsync(configuration.ScheduleId, configuration);

        var dispatched = mutation == ScheduleMutationKind.Ensure
            ? actorPort.Ensured.Should().ContainSingle().Which.Configuration
            : actorPort.Updated.Should().ContainSingle().Which.Configuration;
        dispatched.Target.ServiceInvocation!.Auth.Should().BeNull();
        dispatched.CredentialRequirementTargetKind.Should()
            .Be(ScheduledDispatchCredentialRequirementTargetKind.WorkflowService);
    }

    [Fact]
    public async Task EnsureAsync_ForNewWorkflowServiceWithoutCredentialSource_ShouldRejectBeforeDispatch()
    {
        var actorPort = new RecordingScheduledDispatchActorPort();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort());

        var act = () => service.EnsureAsync(CreateServiceInvocationConfiguration(
            "schedule-workflow-new-no-auth",
            ScheduledDispatchScheduleKind.Workflow,
            ScheduledDispatchCredentialRequirementTargetKind.WorkflowService));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*requires a typed service invocation credential source*");
        actorPort.Ensured.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_ShouldAllowStaticServiceInvocationWithoutCredentialSource()
    {
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort());

        await service.CreateAsync(CreateServiceInvocationConfiguration(
            "schedule-static-no-auth",
            ScheduledDispatchScheduleKind.Generic,
            ScheduledDispatchCredentialRequirementTargetKind.StaticService));

        actorPort.Created.Should().ContainSingle()
            .Which.Configuration.CredentialRequirementTargetKind.Should()
            .Be(ScheduledDispatchCredentialRequirementTargetKind.StaticService);
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectCurrentSessionCredentialsBeforeActorDispatch()
    {
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort());

        var payloadCredential = () => service.CreateAsync(CreateServiceInvocationConfiguration(
            "schedule-payload-credential",
            ScheduledDispatchScheduleKind.Generic,
            ScheduledDispatchCredentialRequirementTargetKind.StaticService,
            payload: Any.Pack(new ChatRequestEvent
            {
                LlmControl = new LLMControlContextPayload
                {
                    SenderNyxIdAccessToken = "sender-token",
                },
            })));
        var headerCredential = () => service.CreateAsync(CreateEnvelopeConfiguration("schedule-header-credential") with
        {
            CredentialRequirementTargetKind = ScheduledDispatchCredentialRequirementTargetKind.Envelope,
            Headers = new Dictionary<string, string>
            {
                [ScheduledDispatchCredentialRequirementRequests.LegacyConnectorHttpAuthorizationHeader] = "Bearer current-token",
            },
        });

        await payloadCredential.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*current-session credentials*");
        await headerCredential.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*current-session credentials*");
        actorPort.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_ShouldNormalizeServiceInvocationAndDispatchUpdate()
    {
        var actorPort = new RecordingScheduledDispatchActorPort();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(), new NoopScheduledDispatchCredentialAdmissionPort());
        var payload = Any.Pack(new StringValue { Value = "invoke" });

        var receipt = await service.UpdateAsync(
            " schedule-1 ",
            new ScheduledDispatchConfiguration(
                "ignored",
                "Invoke",
                new ScheduledDispatchTargetDescriptor(
                    ScheduledDispatchTargetKind.ServiceInvocation,
                    ActorId: "must-clear",
                    Envelope: new EventEnvelope { Payload = Any.Pack(new Empty()) },
                    ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                        new ServiceIdentity
                        {
                            TenantId = " tenant ",
                            AppId = " app ",
                            Namespace = " default ",
                            ServiceId = " svc ",
                        },
                        " run ",
                        payload,
                        " rev-1 ")),
                "0 10 * * *",
                null,
                false,
                new Dictionary<string, string>()));

        receipt.ScheduleId.Should().Be("schedule-1");
        actorPort.EnsuredScheduleIds.Should().BeEmpty();
        var updated = actorPort.Updated.Should().ContainSingle().Which;
        updated.ActorId.Should().Be("actor:schedule-1");
        updated.Configuration.Target.ActorId.Should().BeNull();
        updated.Configuration.Target.Envelope.Should().BeNull();
        updated.Configuration.Target.ServiceInvocation.Should().NotBeNull();
        updated.Configuration.Target.ServiceInvocation!.EndpointId.Should().Be("run");
        updated.Configuration.Target.ServiceInvocation.RevisionId.Should().Be("rev-1");
        updated.Configuration.Target.ServiceInvocation.Identity.TenantId.Should().Be("tenant");
        updated.Configuration.Target.ServiceInvocation.Identity.AppId.Should().Be("app");
        updated.Configuration.Target.ServiceInvocation.Identity.Namespace.Should().Be("default");
        updated.Configuration.Target.ServiceInvocation.Identity.ServiceId.Should().Be("svc");
        updated.Configuration.Timezone.Should().Be("UTC");
        updated.Dispatch.TargetActorId.Should().Be(ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId);
        var invocation = updated.Dispatch.TriggerEnvelope.Payload.Unpack<ServiceInvocationRequest>();
        invocation.Identity.TenantId.Should().Be("tenant");
        invocation.Identity.AppId.Should().Be("app");
        invocation.Identity.Namespace.Should().Be("default");
        invocation.Identity.ServiceId.Should().Be("svc");
    }

    [Fact]
    public async Task EnsureAsync_ShouldNormalizePrepareEnsureActorAndDispatch()
    {
        var actorPort = new RecordingScheduledDispatchActorPort();
        var queryPort = new RecordingScheduledDispatchQueryPort();
        var preparation = new ScheduledDispatchTargetPreparationService();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            queryPort,
            preparation,
            new NoopScheduledDispatchCredentialAdmissionPort());

        var receipt = await service.EnsureAsync(CreateEnvelopeConfiguration(" schedule-1 "));

        receipt.ScheduleId.Should().Be("schedule-1");
        receipt.ScheduleActorId.Should().Be("actor:schedule-1");
        receipt.AckStage.Should().Be("accepted");
        actorPort.EnsuredScheduleIds.Should().ContainSingle().Which.Should().Be("schedule-1");
        actorPort.Ensured.Should().ContainSingle();
        actorPort.Created.Should().BeEmpty();
        actorPort.Updated.Should().BeEmpty();
        // The single readmodel read is the delete-tombstone guard: the actor
        // rejects reconfiguring a deleted schedule and the admission-only
        // dispatch would swallow that rejection, so ensure must check first.
        queryPort.GetScheduleIds.Should().ContainSingle().Which.Should().Be("schedule-1");
        queryPort.ListRequests.Should().BeEmpty();
        queryPort.FilteredListRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(ScheduleMutationKind.Create), false)]
    [InlineData(nameof(ScheduleMutationKind.Create), true)]
    [InlineData(nameof(ScheduleMutationKind.Ensure), false)]
    [InlineData(nameof(ScheduleMutationKind.Ensure), true)]
    public async Task GenericCreationMutations_ShouldRejectTeamOwnerBeforeAnyDownstreamCall(
        string mutationName,
        bool ownerOnConfiguration)
    {
        var mutation = System.Enum.Parse<ScheduleMutationKind>(mutationName);
        var owner = new TeamMemberAutomationOwner("scope-alpha", "member-alpha");
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var queryPort = new RecordingScheduledDispatchQueryPort();
        var preparation = new RecordingScheduledDispatchTargetPreparationService();
        var admissionPort = new RecordingScheduledDispatchCredentialAdmissionPort();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            queryPort,
            preparation,
            admissionPort);
        var configuration = CreateScopeOwnerConfiguration() with
        {
            TeamAutomationOwner = ownerOnConfiguration ? owner : null,
        };
        var context = CreateScopeOwnerContext() with { TeamAutomationOwner = owner };

        var act = () => ExecuteMutationAsync(service, mutation, configuration, context);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*dedicated team automation lifecycle*");
        admissionPort.Requests.Should().BeEmpty();
        preparation.Requests.Should().BeEmpty();
        AssertNoActorMutationDispatch(actorPort);
        AssertNoScheduleQuery(queryPort);
    }

    [Theory]
    [InlineData(nameof(ScheduleMutationKind.Create))]
    [InlineData(nameof(ScheduleMutationKind.Ensure))]
    public async Task GenericCreationMutations_WithoutTeamOwner_ShouldKeepLegacyBehavior(string mutationName)
    {
        var mutation = System.Enum.Parse<ScheduleMutationKind>(mutationName);
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var preparation = new RecordingScheduledDispatchTargetPreparationService();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            preparation,
            new NoopScheduledDispatchCredentialAdmissionPort());
        var context = new ScheduledDispatchMutationContext(
            "scope-alpha",
            new ScheduledServiceInvocationNyxIdSubjectRef(
                OwnerScope.NyxIdPlatform,
                string.Empty,
                "owner-alpha"));

        await ExecuteMutationAsync(service, mutation, CreateEnvelopeConfiguration("schedule-legacy"), context);

        preparation.Requests.Should().ContainSingle();
        if (mutation == ScheduleMutationKind.Create)
            actorPort.Created.Should().ContainSingle();
        else
            actorPort.Ensured.Should().ContainSingle();
    }

    [Fact]
    public async Task UpdateAsync_WhenScheduleMissing_ShouldThrowNotFoundWithoutEnsuringActor()
    {
        var actorPort = new RecordingScheduledDispatchActorPort();
        actorPort.MissingScheduleIds.Add("missing");
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(), new NoopScheduledDispatchCredentialAdmissionPort());

        var act = () => service.UpdateAsync(" missing ", CreateEnvelopeConfiguration("ignored"));

        await act.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        actorPort.EnsuredScheduleIds.Should().BeEmpty();
        actorPort.Updated.Should().BeEmpty();
    }

    [Theory]
    [InlineData("tenant/report")]
    [InlineData("tenant?report")]
    [InlineData("tenant report")]
    public async Task CreateAsync_ShouldRejectRouteUnsafeScheduleId(string scheduleId)
    {
        var service = CreateService();

        var act = () => service.CreateAsync(CreateEnvelopeConfiguration(scheduleId));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*letters, digits, '.', '_', and '-'*");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("ABC")]
    [InlineData("abc-123_DEF.ghi.jkl")]
    public void ScheduledDispatchActorIdFormat_ShouldAcceptAsciiScheduleIds(string scheduleId)
    {
        ScheduledDispatchActorId.Format($" {scheduleId} ")
            .Should().Be($"scheduled-dispatch:{scheduleId}");
    }

    [Theory]
    [InlineData("计划")]
    [InlineData("éclair")]
    [InlineData("emoji-🙂")]
    public void ScheduledDispatchActorIdFormat_ShouldRejectNonAsciiScheduleIds(string scheduleId)
    {
        var act = () => ScheduledDispatchActorId.Format(scheduleId);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*letters, digits, '.', '_', and '-'*");
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(" scheduled-dispatch:schedule-1 ", "schedule-1")]
    [InlineData("external-actor", "external-actor")]
    public void ScheduledDispatchActorIdUnformat_ShouldReturnUserScheduleId(string actorId, string expected)
    {
        ScheduledDispatchActorId.Unformat(actorId).Should().Be(expected);
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectInvalidTargetShapes()
    {
        var service = CreateService();

        var missingEnvelopePayload = () => service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-1",
            string.Empty,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.Envelope,
                ActorId: "actor-1",
                Envelope: new EventEnvelope()),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()));
        var missingServiceId = () => service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-2",
            string.Empty,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default" },
                    "run",
                    Any.Pack(new Empty()))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()));
        var missingEndpoint = () => service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-3",
            string.Empty,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    " ",
                    Any.Pack(new Empty()))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()));

        await missingEnvelopePayload.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*envelope payload*");
        await missingServiceId.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*service id*");
        await missingEndpoint.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*endpoint id*");
    }

    [Theory]
    [InlineData(" ", "app", "default", "svc", "tenant id")]
    [InlineData("tenant", " ", "default", "svc", "app id")]
    [InlineData("tenant", "app", " ", "svc", "namespace")]
    [InlineData("tenant", "app", "default", " ", "service id")]
    public async Task CreateAsync_ShouldRejectIncompleteServiceInvocationIdentity(
        string tenantId,
        string appId,
        string serviceNamespace,
        string serviceId,
        string expectedMessage)
    {
        var service = CreateService();

        var act = () => service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-identity",
            string.Empty,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity
                    {
                        TenantId = tenantId,
                        AppId = appId,
                        Namespace = serviceNamespace,
                        ServiceId = serviceId,
                    },
                    "run",
                    Any.Pack(new Empty()))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage($"*{expectedMessage}*");
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectEmptyServiceInvocationAuth()
    {
        var service = CreateService();

        var act = () => service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-auth",
            string.Empty,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    "run",
                    Any.Pack(new Empty()),
                    Auth: new ScheduledServiceInvocationAuth())),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectServiceInvocationAuthWithInvalidNyxIdRole()
    {
        var service = CreateService();

        var act = () => service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-auth",
            string.Empty,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    "run",
                    Any.Pack(new Empty()),
                    Auth: new ScheduledServiceInvocationAuth(
                        new ScheduledServiceInvocationNyxIdCredentialSource(
                            new ScheduledServiceInvocationNyxIdSubjectRef("lark", "tenant-1", "ou-user-1"),
                            "proxy",
                            (ScheduledServiceInvocationNyxIdCredentialRole)999)))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*NyxID credential role is required*");
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectScopeOwnerServiceInvocationAuthWithoutMutationContext()
    {
        var service = CreateService();

        var act = () => service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-owner-auth",
            string.Empty,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    "run",
                    Any.Pack(new Empty()),
                    Auth: new ScheduledServiceInvocationAuth(
                        ScopeOwnerNyxId: new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource("owner-proxy")))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Authenticated NyxID owner subject*");
    }

    [Fact]
    public async Task CreateAsync_ShouldStampScopeOwnerServiceInvocationAuthFromMutationContext()
    {
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var admissionPort = new RecordingScheduledDispatchCredentialAdmissionPort();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(),
            admissionPort);
        var context = new ScheduledDispatchMutationContext(
            "tenant",
            new ScheduledServiceInvocationNyxIdSubjectRef(OwnerScope.NyxIdPlatform, string.Empty, "owner-nyx-user"));

        await service.CreateAsync(new ScheduledDispatchConfiguration(
            "schedule-owner-auth",
            "Invoke",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    "run",
                    Any.Pack(new StringValue { Value = "invoke" }),
                    Auth: new ScheduledServiceInvocationAuth(
                        ScopeOwnerNyxId: new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource(
                            " owner-proxy ")))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>()),
            context);

        var created = actorPort.Created.Should().ContainSingle().Which;
        var configurationOwnerAuth = created.Configuration.Target.ServiceInvocation!.Auth!.NyxId!;
        configurationOwnerAuth.Role.Should().Be(ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner);
        configurationOwnerAuth.Scope.Should().Be("owner-proxy");
        configurationOwnerAuth.Subject.Should().BeEquivalentTo(
            new ScheduledServiceInvocationNyxIdSubjectRef(OwnerScope.NyxIdPlatform, string.Empty, "owner-nyx-user"));
        var dispatchOwnerAuth = created.Dispatch.Descriptor.ServiceInvocation!.Auth!.NyxId!;
        dispatchOwnerAuth.Role.Should().Be(ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner);
        dispatchOwnerAuth.Scope.Should().Be("owner-proxy");
        dispatchOwnerAuth.Subject.Should().BeEquivalentTo(
            new ScheduledServiceInvocationNyxIdSubjectRef(OwnerScope.NyxIdPlatform, string.Empty, "owner-nyx-user"));
        admissionPort.Requests.Should().ContainSingle()
            .Which.ScopeOwnerNyxId.OwnerSubject.Should().BeEquivalentTo(context.AuthenticatedNyxIdOwnerSubject);
    }

    [Theory]
    [InlineData(nameof(ScheduleMutationKind.Create))]
    [InlineData(nameof(ScheduleMutationKind.Ensure))]
    [InlineData(nameof(ScheduleMutationKind.Update))]
    public async Task ScopeOwnerMutations_ShouldRejectMissingBindingBeforeDispatch(string mutationName)
    {
        var mutation = System.Enum.Parse<ScheduleMutationKind>(mutationName);
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var admissionPort = new RecordingScheduledDispatchCredentialAdmissionPort
        {
            Result = ScheduledDispatchCredentialAdmissionResult.MissingBinding(),
        };
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(),
            admissionPort);
        var configuration = CreateScopeOwnerConfiguration();
        var context = CreateScopeOwnerContext();

        var act = () => ExecuteMutationAsync(service, mutation, configuration, context);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*owner binding is required*");
        admissionPort.Requests.Should().ContainSingle();
        actorPort.Created.Should().BeEmpty();
        actorPort.Ensured.Should().BeEmpty();
        actorPort.Updated.Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(ScheduleMutationKind.Create), null)]
    [InlineData(nameof(ScheduleMutationKind.Create), "")]
    [InlineData(nameof(ScheduleMutationKind.Create), " ")]
    [InlineData(nameof(ScheduleMutationKind.Ensure), null)]
    [InlineData(nameof(ScheduleMutationKind.Ensure), "")]
    [InlineData(nameof(ScheduleMutationKind.Ensure), " ")]
    [InlineData(nameof(ScheduleMutationKind.Update), null)]
    [InlineData(nameof(ScheduleMutationKind.Update), "")]
    [InlineData(nameof(ScheduleMutationKind.Update), " ")]
    public async Task ScopeOwnerMutations_ShouldRejectMissingAuthenticatedScopeBeforeAdmission(
        string mutationName,
        string? authenticatedScopeId)
    {
        var mutation = System.Enum.Parse<ScheduleMutationKind>(mutationName);
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var queryPort = new RecordingScheduledDispatchQueryPort();
        var admissionPort = new RecordingScheduledDispatchCredentialAdmissionPort();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            queryPort,
            new ScheduledDispatchTargetPreparationService(),
            admissionPort);
        var context = CreateScopeOwnerContext() with
        {
            AuthenticatedScopeId = authenticatedScopeId,
        };

        var act = () => ExecuteMutationAsync(service, mutation, CreateScopeOwnerConfiguration(), context);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*AuthenticatedScopeId is required*");
        admissionPort.Requests.Should().BeEmpty();
        AssertNoActorMutationDispatch(actorPort);
        AssertNoScheduleQuery(queryPort);
    }

    [Theory]
    [InlineData(nameof(ScheduleMutationKind.Create))]
    [InlineData(nameof(ScheduleMutationKind.Ensure))]
    [InlineData(nameof(ScheduleMutationKind.Update))]
    public async Task ScopeOwnerMutations_ShouldMapScopeMismatchAdmissionBeforeDispatch(string mutationName)
    {
        var mutation = System.Enum.Parse<ScheduleMutationKind>(mutationName);
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var queryPort = new RecordingScheduledDispatchQueryPort();
        var admissionPort = new RecordingScheduledDispatchCredentialAdmissionPort
        {
            Result = ScheduledDispatchCredentialAdmissionResult.ScopeMismatch(),
        };
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            queryPort,
            new ScheduledDispatchTargetPreparationService(),
            admissionPort);

        var act = () => ExecuteMutationAsync(
            service,
            mutation,
            CreateScopeOwnerConfiguration(),
            CreateScopeOwnerContext());

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*NyxID binding does not grant the requested schedule scope*");
        admissionPort.Requests.Should().ContainSingle();
        AssertNoActorMutationDispatch(actorPort);
        AssertNoScheduleQuery(queryPort);
    }

    [Theory]
    [InlineData(nameof(ScheduleMutationKind.Create))]
    [InlineData(nameof(ScheduleMutationKind.Ensure))]
    [InlineData(nameof(ScheduleMutationKind.Update))]
    public async Task ScopeOwnerMutations_WithNoopAdmission_ShouldRejectBeforeDispatch(string mutationName)
    {
        var mutation = System.Enum.Parse<ScheduleMutationKind>(mutationName);
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var queryPort = new RecordingScheduledDispatchQueryPort();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            queryPort,
            new ScheduledDispatchTargetPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort());
        var configuration = CreateScopeOwnerConfiguration();
        var context = CreateScopeOwnerContext();

        var act = () => ExecuteMutationAsync(service, mutation, configuration, context);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*admission is not configured*");
        actorPort.ResolvedScheduleIds.Should().BeEmpty();
        actorPort.EnsuredScheduleIds.Should().BeEmpty();
        actorPort.Created.Should().BeEmpty();
        actorPort.Ensured.Should().BeEmpty();
        actorPort.Updated.Should().BeEmpty();
        queryPort.GetScheduleIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(ScheduleMutationKind.Create))]
    [InlineData(nameof(ScheduleMutationKind.Ensure))]
    [InlineData(nameof(ScheduleMutationKind.Update))]
    public async Task ScopeOwnerMutations_ShouldRejectTargetScopeMismatchBeforeAdmission(string mutationName)
    {
        var mutation = System.Enum.Parse<ScheduleMutationKind>(mutationName);
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var admissionPort = new RecordingScheduledDispatchCredentialAdmissionPort();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(),
            admissionPort);
        var configuration = CreateScopeOwnerConfiguration(tenantId: "scope-2");
        var context = CreateScopeOwnerContext();

        var act = () => ExecuteMutationAsync(service, mutation, configuration, context);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*target scope must match the authenticated scope*");
        admissionPort.Requests.Should().BeEmpty();
        actorPort.Created.Should().BeEmpty();
        actorPort.Ensured.Should().BeEmpty();
        actorPort.Updated.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectScopeOwnerSubjectMismatchBeforeAdmission()
    {
        var actorPort = new RecordingScheduledDispatchActorPort { ResolveUnknownAsMissing = true };
        var admissionPort = new RecordingScheduledDispatchCredentialAdmissionPort();
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(),
            admissionPort);
        var configuration = CreateScopeOwnerConfiguration(
            ownerSubject: new ScheduledServiceInvocationNyxIdSubjectRef(OwnerScope.NyxIdPlatform, string.Empty, "evil-owner"));

        var act = () => service.CreateAsync(configuration, CreateScopeOwnerContext());

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*must match the authenticated owner subject*");
        admissionPort.Requests.Should().BeEmpty();
        actorPort.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task NoopScheduledServiceInvocationCredentialExchangePort_ShouldRejectScopeOwnerExchange()
    {
        var port = new NoopScheduledServiceInvocationCredentialExchangePort();

        var result = await port.IssueNyxIdAsync(new ScheduledServiceInvocationNyxIdCredentialSource(
            new ScheduledServiceInvocationNyxIdSubjectRef(OwnerScope.NyxIdPlatform, string.Empty, "owner-nyx-user"),
            "owner-proxy",
            ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner));

        result.Succeeded.Should().BeFalse();
        result.AccessToken.Should().BeNull();
        result.Error.Should().Be("Scheduled service invocation scope owner NyxID credential exchange is not configured.");
    }

    [Fact]
    public async Task ScheduledDispatchActorPort_ShouldCreateActorWhenMissingAndDispatchServiceInvocationCommands()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var port = new ScheduledDispatchActorPort(runtime, dispatchPort);
        var configuration = new ScheduledDispatchConfiguration(
            "schedule-1",
            "Invoke",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    "run",
                    Any.Pack(new StringValue { Value = "invoke" }),
                    "rev-1",
                    new ServiceInvocationCaller { ServiceKey = "tenant:app:default:caller", TenantId = "tenant", AppId = "app" },
                    new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
                        new ScheduledServiceInvocationNyxIdSubjectRef("lark", "tenant-1", "ou-user-1"),
                        "proxy")))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string> { ["trace"] = "scheduled" },
            ScheduledDispatchScheduleKind.Workflow)
        {
            CredentialRequirementTargetKind = ScheduledDispatchCredentialRequirementTargetKind.WorkflowService,
        };
        var prepared = await new ScheduledDispatchTargetPreparationService()
            .PrepareAsync(configuration, "cmd-1", "corr-1");

        var actorId = await port.EnsureScheduleActorAsync("schedule-1");
        var receipt = await port.DispatchCreateAsync(actorId, configuration, prepared);

        actorId.Should().Be("scheduled-dispatch:schedule-1");
        runtime.CreatedIds.Should().ContainSingle().Which.Should().Be("scheduled-dispatch:schedule-1");
        receipt.Accepted.Should().BeTrue();
        var command = dispatchPort.Envelopes.Should().ContainSingle().Which.Payload.Unpack<ScheduledDispatchCreateCommand>();
        command.ScheduleId.Should().Be("schedule-1");
        command.Headers.Should().Contain("trace", "scheduled");
        command.Target.Kind.Should().Be(ScheduledDispatchTargetKindState.ServiceInvocation);
        command.Target.CredentialRequirementTargetKind.Should().Be(
            ScheduledDispatchCredentialRequirementTargetKindState.WorkflowService);
        command.Target.ServiceInvocation.EndpointId.Should().Be("run");
        command.Target.ServiceInvocation.Auth.NyxId.Role.Should().Be(ScheduledServiceInvocationNyxIdCredentialRoleState.Sender);
        command.Target.ServiceInvocation.Auth.NyxId.Subject.ExternalUserId.Should().Be("ou-user-1");
        command.ScheduleKind.Should().Be(ScheduledDispatchScheduleKindState.Workflow);
    }

    [Fact]
    public async Task ScheduledDispatchActorPort_ShouldPersistScopeOwnerNyxIdAuth()
    {
        var dispatchPort = new RecordingActorDispatchPort();
        var port = new ScheduledDispatchActorPort(new RecordingActorRuntime(), dispatchPort);
        var configuration = new ScheduledDispatchConfiguration(
            "schedule-owner",
            "Invoke",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "owner-nyx-user", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    "run",
                    Any.Pack(new StringValue { Value = "invoke" }),
                    Auth: new ScheduledServiceInvocationAuth(
                        ScopeOwnerNyxId: new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource(
                            "proxy",
                            new ScheduledServiceInvocationNyxIdSubjectRef(OwnerScope.NyxIdPlatform, string.Empty, "owner-nyx-user"))))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>(),
            ScheduledDispatchScheduleKind.Workflow);
        var prepared = await new ScheduledDispatchTargetPreparationService()
            .PrepareAsync(configuration, "cmd-1", "corr-1");

        await port.DispatchCreateAsync("scheduled-dispatch:schedule-owner", configuration, prepared);

        var command = dispatchPort.Envelopes.Should().ContainSingle().Which.Payload.Unpack<ScheduledDispatchCreateCommand>();
        command.Target.ServiceInvocation.Auth.SenderNyxId.Should().BeNull();
        command.Target.ServiceInvocation.Auth.ScopeOwnerNyxId.Should().BeNull();
        command.Target.ServiceInvocation.Auth.NyxId.Role.Should().Be(ScheduledServiceInvocationNyxIdCredentialRoleState.ScopeOwner);
        command.Target.ServiceInvocation.Auth.NyxId.Scope.Should().Be("proxy");
        command.Target.ServiceInvocation.Auth.NyxId.Subject.Should().BeEquivalentTo(
            new ScheduledServiceInvocationNyxIdSubjectRefState
            {
                Platform = OwnerScope.NyxIdPlatform,
                Tenant = string.Empty,
                ExternalUserId = "owner-nyx-user",
            });
    }

    [Fact]
    public async Task ScheduledDispatchActorPort_ShouldPersistScheduledInvocationAgentKeyReference()
    {
        var dispatchPort = new RecordingActorDispatchPort();
        var port = new ScheduledDispatchActorPort(new RecordingActorRuntime(), dispatchPort);
        var reference = CreateScheduledInvocationAgentKeyReference();
        var configuration = new ScheduledDispatchConfiguration(
            "schedule-agent-key",
            "Invoke",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    "run",
                    Any.Pack(new StringValue { Value = "invoke" }),
                    Auth: new ScheduledServiceInvocationAuth(ScheduledInvocationAgentKey: reference))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>(),
            ScheduledDispatchScheduleKind.Workflow);
        var prepared = await new ScheduledDispatchTargetPreparationService()
            .PrepareAsync(configuration, "cmd-1", "corr-1");

        await port.DispatchCreateAsync("scheduled-dispatch:schedule-agent-key", configuration, prepared);

        var command = dispatchPort.Envelopes.Should().ContainSingle().Which.Payload.Unpack<ScheduledDispatchCreateCommand>();
        var auth = command.Target.ServiceInvocation.Auth;
        auth.DurableSenderBearerToken.Should().BeEmpty();
        auth.LegacyDurableSenderBearerBlocked.Should().BeFalse();
        auth.ScheduledInvocationAgentKey.Should().NotBeNull();
        auth.ScheduledInvocationAgentKey.SecretReference.Purpose.Should().Be(CredentialSecretPurposes.ScheduledInvocationAgentKey);
        auth.ScheduledInvocationAgentKey.ApiKeyId.Should().Be("key-schedule");
        auth.ScheduledInvocationAgentKey.KeyExpiresAtUnixMs.Should().Be(reference.KeyExpiresAtUnixMs);
    }

    [Fact]
    public async Task ScheduledDispatchActorPort_ShouldPersistDurableCredentialReferenceAuth()
    {
        var dispatchPort = new RecordingActorDispatchPort();
        var port = new ScheduledDispatchActorPort(new RecordingActorRuntime(), dispatchPort);
        var configuration = new ScheduledDispatchConfiguration(
            "schedule-durable",
            "Invoke",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    "run",
                    Any.Pack(new StringValue { Value = "invoke" }),
                    Auth: new ScheduledServiceInvocationAuth(CreateDurableCredentialReference()))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>(),
            ScheduledDispatchScheduleKind.Workflow);
        var prepared = await new ScheduledDispatchTargetPreparationService()
            .PrepareAsync(configuration, "cmd-1", "corr-1");

        await port.DispatchCreateAsync("scheduled-dispatch:schedule-durable", configuration, prepared);

        var command = dispatchPort.Envelopes.Should().ContainSingle().Which.Payload.Unpack<ScheduledDispatchCreateCommand>();
        command.Target.ServiceInvocation.Auth.SourceCase.Should().Be(ScheduledServiceInvocationAuthState.SourceOneofCase.Durable);
        command.Target.ServiceInvocation.Auth.Durable.Should().NotBeNull();
        command.Target.ServiceInvocation.Auth.Durable.CredentialId.Should().Be("credential-1");
        command.Target.ServiceInvocation.Auth.Durable.SecretReference.Ref.Should().Be("sec-1");
        command.Target.ServiceInvocation.Auth.Durable.SecretReference.OwnerScopeKey.Should().Be("owner-scope-1");
    }

    [Fact]
    public async Task ScheduledDispatchActorPort_ShouldResolveExistingActorAndDispatchLifecycleCommands()
    {
        var runtime = new RecordingActorRuntime();
        runtime.ExistingActors["scheduled-dispatch:schedule-1"] = new RecordingActor("scheduled-dispatch:schedule-1");
        var dispatchPort = new RecordingActorDispatchPort();
        var port = new ScheduledDispatchActorPort(runtime, dispatchPort);

        var ensured = await port.EnsureScheduleActorAsync("schedule-1");
        var resolved = await port.ResolveScheduleActorAsync("schedule-1");
        var missing = await port.ResolveScheduleActorAsync("missing");
        await port.DispatchEnableAsync(ensured, null!);
        await port.DispatchDisableAsync(ensured, "pause");
        await port.DispatchRunNowAsync(ensured, new DateTimeOffset(2026, 6, 9, 8, 0, 0, TimeSpan.Zero));

        ensured.Should().Be("scheduled-dispatch:schedule-1");
        resolved.Should().Be("scheduled-dispatch:schedule-1");
        missing.Should().BeNull();
        runtime.CreatedIds.Should().BeEmpty();
        dispatchPort.Envelopes[0].Payload.Unpack<ScheduledDispatchEnableCommand>().Reason.Should().BeEmpty();
        dispatchPort.Envelopes[1].Payload.Unpack<ScheduledDispatchDisableCommand>().Reason.Should().Be("pause");
        var fire = dispatchPort.Envelopes[2].Payload.Unpack<ScheduledDispatchFireCommand>();
        fire.Manual.Should().BeTrue();
        fire.ScheduledFireAt.ToDateTimeOffset().Should().Be(new DateTimeOffset(2026, 6, 9, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task ScheduledDispatchActorPort_ShouldMapOneShotScheduleMode()
    {
        var dispatchPort = new RecordingActorDispatchPort();
        var port = new ScheduledDispatchActorPort(new RecordingActorRuntime(), dispatchPort);
        var fireAt = new DateTimeOffset(2026, 7, 14, 1, 30, 0, TimeSpan.Zero);
        var configuration = CreateEnvelopeConfiguration("one-shot-1") with
        {
            CronExpression = string.Empty,
            ScheduleMode = ScheduledDispatchScheduleMode.OneShotAtUtc,
            OneShotFireAt = fireAt,
        };
        var prepared = await new ScheduledDispatchTargetPreparationService()
            .PrepareAsync(configuration, "cmd-1", "corr-1");

        await port.DispatchEnsureAsync("scheduled-dispatch:one-shot-1", configuration, prepared);

        var command = dispatchPort.Envelopes.Should().ContainSingle().Which.Payload.Unpack<ScheduledDispatchEnsureCommand>();
        command.ScheduleMode.Should().Be(ScheduledDispatchScheduleModeState.OneShotAtUtc);
        command.OneShotFireAt.ToDateTimeOffset().Should().Be(fireAt);
        command.CronExpression.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledDispatchActorPort_ShouldMapEnvelopeUpdateAndRejectUnsupportedTarget()
    {
        var dispatchPort = new RecordingActorDispatchPort();
        var port = new ScheduledDispatchActorPort(new RecordingActorRuntime(), dispatchPort);
        var configuration = CreateEnvelopeConfiguration("schedule-1");
        var prepared = await new ScheduledDispatchTargetPreparationService()
            .PrepareAsync(configuration, "cmd-1", "corr-1");

        await port.DispatchUpdateAsync("scheduled-dispatch:schedule-1", configuration, prepared);
        var unsupported = () => port.DispatchUpdateAsync(
            "scheduled-dispatch:schedule-1",
            configuration,
            new PreparedScheduledDispatchTarget(
                null,
                new EventEnvelope { Payload = Any.Pack(new Empty()) },
                Any.Pack(new Empty()).TypeUrl,
                new ScheduledDispatchTargetDescriptor((ScheduledDispatchTargetKind)99)));

        var command = dispatchPort.Envelopes.Should().ContainSingle().Which.Payload.Unpack<ScheduledDispatchUpdateCommand>();
        command.Target.Kind.Should().Be(ScheduledDispatchTargetKindState.Envelope);
        command.Target.ActorId.Should().Be("actor-1");
        command.Target.Envelope.Payload.Unpack<Empty>().Should().NotBeNull();
        command.ScheduleKind.Should().Be(ScheduledDispatchScheduleKindState.Generic);
        await unsupported.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Unsupported scheduled dispatch target kind*");
    }

    [Fact]
    public async Task EnableDisableDeleteRunNow_ShouldResolveExistingActorAndReturnNotFoundWhenMissing()
    {
        var actorPort = new RecordingScheduledDispatchActorPort();
        var queryPort = new RecordingScheduledDispatchQueryPort
        {
            Detail = CreateSummaryDetail(
                "schedule-1",
                ScheduledDispatchTargetKind.Envelope,
                ScheduledDispatchScheduleKind.Generic,
                ScheduledDispatchCredentialRequirementTargetKind.Envelope,
                ScheduledDispatchCredentialSourceKind.None),
        };
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            queryPort,
            new ScheduledDispatchTargetPreparationService(), new NoopScheduledDispatchCredentialAdmissionPort());

        var enabled = await service.EnableAsync(" schedule-1 ", " resume ");
        var disabled = await service.DisableAsync("schedule-1", null!);
        var deleted = await service.DeleteAsync(" schedule-1 ", " remove ");
        var runNow = await service.RunNowAsync("schedule-1");
        actorPort.MissingScheduleIds.Add("missing");
        var missing = () => service.RunNowAsync("missing");

        enabled.Should().BeEquivalentTo(new
        {
            ScheduleId = "schedule-1",
            ScheduleActorId = "actor:schedule-1",
            Accepted = true,
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
            AckStage = "accepted",
        });
        enabled.AckedAt.Should().NotBe(default);
        disabled.Should().BeEquivalentTo(new
        {
            ScheduleId = "schedule-1",
            ScheduleActorId = "actor:schedule-1",
            Accepted = true,
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
            AckStage = "accepted",
        });
        disabled.AckedAt.Should().NotBe(default);
        deleted.Should().BeEquivalentTo(new
        {
            ScheduleId = "schedule-1",
            ScheduleActorId = "actor:schedule-1",
            Accepted = true,
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
            AckStage = "accepted",
        });
        deleted.AckedAt.Should().NotBe(default);
        runNow.ScheduleId.Should().Be("schedule-1");
        runNow.ScheduleActorId.Should().Be("actor:schedule-1");
        runNow.CommandId.Should().Be("cmd-1");
        runNow.CorrelationId.Should().Be("corr-1");
        runNow.AckedAt.Should().NotBe(default);
        runNow.AckStage.Should().Be("accepted");
        runNow.IdempotencyKey.Should().Be(
            ScheduledDispatchCalculator.BuildIdempotencyKey("schedule-1", runNow.ScheduledFireAt));
        actorPort.Enabled.Should().ContainSingle().Which.Should().Be(("actor:schedule-1", "resume"));
        actorPort.Disabled.Should().ContainSingle().Which.Should().Be(("actor:schedule-1", string.Empty));
        actorPort.Deleted.Should().ContainSingle().Which.Should().Be(("actor:schedule-1", "remove"));
        actorPort.RunNow.Should().ContainSingle().Which.ActorId.Should().Be("actor:schedule-1");
        await missing.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
    }

    [Fact]
    public async Task GetAndMutations_ShouldTreatDeletedScheduleAsNotFoundAtApplicationBoundary()
    {
        var actorPort = new RecordingScheduledDispatchActorPort();
        var queryPort = new RecordingScheduledDispatchQueryPort
        {
            Detail = new ScheduledDispatchDetail(
                new ScheduledDispatchSummary(
                    "schedule-1",
                    string.Empty,
                    ScheduledDispatchTargetKind.Envelope,
                    "actor-1",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    "0 9 * * *",
                    "UTC",
                    false,
                    DateTimeOffset.Parse("2026-05-29T08:00:00+00:00"),
                    DateTimeOffset.Parse("2026-05-29T09:00:00+00:00"),
                    null,
                    null,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0,
                    0,
                    new Dictionary<string, string>(),
                    "actor:schedule-1",
                    string.Empty,
                    Deleted: true),
                []),
        };
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            queryPort,
            new ScheduledDispatchTargetPreparationService(), new NoopScheduledDispatchCredentialAdmissionPort());

        var get = await service.GetAsync(" schedule-1 ");
        var update = () => service.UpdateAsync("schedule-1", CreateEnvelopeConfiguration("schedule-1"));
        var ensure = () => service.EnsureAsync(CreateEnvelopeConfiguration("schedule-1"));
        var enable = () => service.EnableAsync("schedule-1", string.Empty);
        var disable = () => service.DisableAsync("schedule-1", string.Empty);
        var delete = () => service.DeleteAsync("schedule-1", string.Empty);
        var runNow = () => service.RunNowAsync("schedule-1");

        get.Should().BeNull();
        await update.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        await ensure.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        await enable.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        await disable.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        await delete.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        await runNow.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        actorPort.Updated.Should().BeEmpty();
        actorPort.Ensured.Should().BeEmpty();
        actorPort.Enabled.Should().BeEmpty();
        actorPort.Disabled.Should().BeEmpty();
        actorPort.Deleted.Should().BeEmpty();
        actorPort.RunNow.Should().BeEmpty();
    }

    [Fact]
    public async Task GenericGetAndMutations_ShouldFailClosedForTeamOwnedScheduleBeforeActorDispatch()
    {
        var actorPort = new RecordingScheduledDispatchActorPort();
        var detail = CreateSummaryDetail(
            "team-schedule",
            ScheduledDispatchTargetKind.Envelope,
            ScheduledDispatchScheduleKind.Generic,
            ScheduledDispatchCredentialRequirementTargetKind.Envelope,
            ScheduledDispatchCredentialSourceKind.None);
        var queryPort = new RecordingScheduledDispatchQueryPort
        {
            Detail = detail with
            {
                Schedule = detail.Schedule with
                {
                    TeamOwned = true,
                    TeamOwnerScopeId = "scope-alpha",
                    TeamOwnerMemberId = "member-alpha",
                },
            },
        };
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            queryPort,
            new ScheduledDispatchTargetPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort());

        (await service.GetAsync("team-schedule")).Should().BeNull();
        var update = () => service.UpdateAsync(
            "team-schedule", CreateEnvelopeConfiguration("team-schedule"));
        var ensure = () => service.EnsureAsync(CreateEnvelopeConfiguration("team-schedule"));
        var enable = () => service.EnableAsync("team-schedule", string.Empty);
        var disable = () => service.DisableAsync("team-schedule", string.Empty);
        var delete = () => service.DeleteAsync("team-schedule", string.Empty);
        var runNow = () => service.RunNowAsync("team-schedule");

        await update.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        await ensure.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        await enable.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        await disable.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        await delete.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        await runNow.Should().ThrowAsync<ScheduledDispatchNotFoundException>();
        actorPort.ResolvedScheduleIds.Should().BeEmpty();
        actorPort.EnsuredScheduleIds.Should().BeEmpty();
        actorPort.Updated.Should().BeEmpty();
        actorPort.Ensured.Should().BeEmpty();
        actorPort.Enabled.Should().BeEmpty();
        actorPort.Disabled.Should().BeEmpty();
        actorPort.Deleted.Should().BeEmpty();
        actorPort.RunNow.Should().BeEmpty();
    }

    [Fact]
    public async Task RunNowAsync_ShouldRejectWorkflowScheduleWithoutCredentialBeforeActorDispatch()
    {
        var actorPort = new RecordingScheduledDispatchActorPort();
        var queryPort = new RecordingScheduledDispatchQueryPort
        {
            Detail = CreateSummaryDetail(
                "schedule-workflow-no-auth",
                ScheduledDispatchTargetKind.ServiceInvocation,
                ScheduledDispatchScheduleKind.Workflow,
                ScheduledDispatchCredentialRequirementTargetKind.WorkflowService,
                ScheduledDispatchCredentialSourceKind.None),
        };
        var service = new ScheduledDispatchApplicationService(
            actorPort,
            queryPort,
            new ScheduledDispatchTargetPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort());

        var act = () => service.RunNowAsync("schedule-workflow-no-auth");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*requires a typed service invocation credential source*");
        actorPort.RunNow.Should().BeEmpty();
    }

    [Fact]
    public async Task GetListAndPreview_ShouldNormalizeInputs()
    {
        var queryPort = new RecordingScheduledDispatchQueryPort();
        var service = new ScheduledDispatchApplicationService(
            new RecordingScheduledDispatchActorPort(),
            queryPort,
            new ScheduledDispatchTargetPreparationService(), new NoopScheduledDispatchCredentialAdmissionPort());

        await service.GetAsync(" schedule-1 ");
        await service.ListAsync(0, "cursor-1", includeTotalCount: true);
        await service.ListAsync(500);
        var preview = await service.PreviewAsync(
            "0 9 * * *",
            null,
            500,
            new DateTimeOffset(2026, 5, 29, 8, 30, 0, TimeSpan.Zero));
        var invalidPreview = () => service.PreviewAsync("invalid", "UTC", 5);

        queryPort.GetScheduleIds.Should().ContainSingle().Which.Should().Be("schedule-1");
        await service.ListAsync(new ScheduledDispatchListQuery(
            25,
            "cursor-2",
            true,
            ScheduledDispatchTargetKind.ServiceInvocation,
            "chat",
            ScheduledDispatchScheduleKind.Workflow));
        queryPort.FilteredListRequests.Should().HaveCount(3);
        queryPort.FilteredListRequests[0].Should().Be(new ScheduledDispatchListQuery(
            1, "cursor-1", true, ExcludeTeamOwned: true));
        queryPort.FilteredListRequests[1].Should().Be(new ScheduledDispatchListQuery(
            200, ExcludeTeamOwned: true));
        queryPort.FilteredListRequests[2].Should().Be(new ScheduledDispatchListQuery(
            25,
            "cursor-2",
            true,
            ScheduledDispatchTargetKind.ServiceInvocation,
            "chat",
            ScheduledDispatchScheduleKind.Workflow,
            ExcludeTeamOwned: true));
        preview.Timezone.Should().Be("UTC");
        preview.NextFireTimes.Should().HaveCount(100);
        await invalidPreview.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ListTeamAutomationsAsync_ShouldApplyVisibilityBeforePagingAndPreserveQueryMetadata()
    {
        var expected = new ScheduledDispatchListResult(
            [CreateSummaryDetail(
                "schedule-visible",
                ScheduledDispatchTargetKind.ServiceInvocation,
                ScheduledDispatchScheduleKind.Workflow,
                ScheduledDispatchCredentialRequirementTargetKind.WorkflowService,
                ScheduledDispatchCredentialSourceKind.ScheduledInvocationAgentKey).Schedule],
            "cursor-visible",
            7);
        var queryPort = new RecordingScheduledDispatchQueryPort { ListResult = expected };
        var service = new ScheduledDispatchApplicationService(
            new RecordingScheduledDispatchActorPort(),
            queryPort,
            new ScheduledDispatchTargetPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort());

        var result = await service.ListTeamAutomationsAsync(
            new TeamMemberAutomationOwner(" scope-alpha ", " member-alpha "),
            25,
            "cursor-input",
            includeTotalCount: true);

        result.Should().BeSameAs(expected);
        queryPort.FilteredListRequests.Should().ContainSingle().Which.Should().Be(
            new ScheduledDispatchListQuery(
                Take: 25,
                Cursor: "cursor-input",
                IncludeTotalCount: true,
                TeamAutomationOwner: new TeamMemberAutomationOwner("scope-alpha", "member-alpha"),
                ExcludeCompletedTeamAutomationDeletions: true));
    }

    [Fact]
    public async Task ScheduledDispatchQueryPort_ShouldApplyTypedFiltersBeforePaging()
    {
        var reader = new RecordingScheduledDispatchDocumentReader
        {
            Result = new ProjectionDocumentQueryResult<ScheduledDispatchDocument>
            {
                Items =
                [
                    new ScheduledDispatchDocument
                    {
                        ScheduleId = "workflow-1",
                        TargetKind = ScheduledDispatchTargetKind.ServiceInvocation.ToString(),
                        ServiceEndpointId = "chat",
                        ServiceId = "daily",
                        ScheduleKind = ScheduledDispatchScheduleKind.Workflow.ToString(),
                        ScheduleMode = ScheduledDispatchScheduleMode.OneShotAtUtc.ToString(),
                        OneShotFireAt = new DateTimeOffset(2026, 7, 14, 1, 30, 0, TimeSpan.Zero),
                        Completed = true,
                    },
                ],
                NextCursor = "workflow-cursor",
                TotalCount = 1,
            },
        };
        var port = new ScheduledDispatchQueryPort(reader);

        var result = await port.ListAsync(new ScheduledDispatchListQuery(
            25,
            "cursor",
            true,
            ScheduledDispatchTargetKind.ServiceInvocation,
            "chat",
            ScheduledDispatchScheduleKind.Workflow));

        var item = result.Items.Should().ContainSingle().Which;
        item.ScheduleId.Should().Be("workflow-1");
        item.ScheduleMode.Should().Be(ScheduledDispatchScheduleMode.OneShotAtUtc);
        item.OneShotFireAt.Should().Be(new DateTimeOffset(2026, 7, 14, 1, 30, 0, TimeSpan.Zero));
        item.Completed.Should().BeTrue();
        result.NextCursor.Should().Be("workflow-cursor");
        result.TotalCount.Should().Be(1);
        reader.LastQuery.Should().NotBeNull();
        reader.LastQuery!.Take.Should().Be(25);
        reader.LastQuery.Cursor.Should().Be("cursor");
        reader.LastQuery.IncludeTotalCount.Should().BeTrue();
        reader.LastQuery.Filters.Should().BeEquivalentTo(
            new[]
            {
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(ScheduledDispatchDocument.Deleted),
                    Operator = ProjectionDocumentFilterOperator.EqOrMissing,
                    Value = ProjectionDocumentValue.FromBool(false),
                },
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(ScheduledDispatchDocument.TargetKind),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString(ScheduledDispatchTargetKind.ServiceInvocation.ToString()),
                },
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(ScheduledDispatchDocument.ServiceEndpointId),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString("chat"),
                },
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(ScheduledDispatchDocument.ScheduleKind),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString(ScheduledDispatchScheduleKind.Workflow.ToString()),
                },
            },
            options => options.ComparingByMembers<ProjectionDocumentValue>());
    }

    [Fact]
    public async Task ScheduledDispatchQueryPort_ShouldFailClosedForUnknownLifecycleStatus()
    {
        var reader = new RecordingScheduledDispatchDocumentReader
        {
            Result = new ProjectionDocumentQueryResult<ScheduledDispatchDocument>
            {
                Items =
                [
                    new ScheduledDispatchDocument
                    {
                        ScheduleId = "team-automation-unknown",
                        TeamOwned = true,
                        TeamAutomationLifecycleStatus = (TeamAutomationLifecycleStatusDocument)999,
                    },
                ],
            },
        };
        var port = new ScheduledDispatchQueryPort(reader);

        var act = () => port.ListAsync(new ScheduledDispatchListQuery(25));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unknown Team automation lifecycle status value '999'*");
    }

    [Fact]
    public async Task ScheduledDispatchQueryPort_ShouldApplyTeamOwnerFilterBeforePagingAndCount()
    {
        var reader = new RecordingScheduledDispatchDocumentReader();
        var port = new ScheduledDispatchQueryPort(reader);
        var owner = new TeamMemberAutomationOwner("scope-alpha", "member-alpha");

        await port.ListAsync(new ScheduledDispatchListQuery(
            Take: 25,
            Cursor: "cursor",
            IncludeTotalCount: true,
            TeamAutomationOwner: owner,
            ExcludeCompletedTeamAutomationDeletions: true));

        reader.LastQuery.Should().NotBeNull();
        reader.LastQuery!.Take.Should().Be(25);
        reader.LastQuery.Cursor.Should().Be("cursor");
        reader.LastQuery.IncludeTotalCount.Should().BeTrue();
        reader.LastQuery.Filters.Should().ContainEquivalentOf(new ProjectionDocumentFilter
        {
            FieldPath = nameof(ScheduledDispatchDocument.TeamOwned),
            Operator = ProjectionDocumentFilterOperator.Eq,
            Value = ProjectionDocumentValue.FromBool(true),
        }, options => options.ComparingByMembers<ProjectionDocumentValue>());
        reader.LastQuery.Filters.Should().ContainEquivalentOf(new ProjectionDocumentFilter
        {
            FieldPath = $"{nameof(ScheduledDispatchDocument.TeamAutomationOwner)}.{nameof(TeamMemberAutomationOwnerDocument.ScopeId)}",
            Operator = ProjectionDocumentFilterOperator.Eq,
            Value = ProjectionDocumentValue.FromString("scope-alpha"),
        }, options => options.ComparingByMembers<ProjectionDocumentValue>());
        reader.LastQuery.Filters.Should().ContainEquivalentOf(new ProjectionDocumentFilter
        {
            FieldPath = $"{nameof(ScheduledDispatchDocument.TeamAutomationOwner)}.{nameof(TeamMemberAutomationOwnerDocument.MemberId)}",
            Operator = ProjectionDocumentFilterOperator.Eq,
            Value = ProjectionDocumentValue.FromString("member-alpha"),
        }, options => options.ComparingByMembers<ProjectionDocumentValue>());
        reader.LastQuery.Filters.Should().NotContain(filter =>
            filter.FieldPath.EndsWith("CurrentTeamId", StringComparison.Ordinal));
        reader.LastQuery.AnyOfFilters.Should().BeEquivalentTo(
            new[]
            {
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(ScheduledDispatchDocument.Deleted),
                    Operator = ProjectionDocumentFilterOperator.EqOrMissing,
                    Value = ProjectionDocumentValue.FromBool(false),
                },
                new ProjectionDocumentFilter
                {
                    FieldPath = nameof(ScheduledDispatchDocument.RevocationPending),
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromBool(true),
                },
            },
            options => options.ComparingByMembers<ProjectionDocumentValue>());
    }

    [Fact]
    public async Task TargetPreparation_ShouldPreserveExistingEnvelopeIdentityAndCorrelation()
    {
        var service = new ScheduledDispatchTargetPreparationService();
        var envelope = new EventEnvelope
        {
            Id = "existing-command",
            Payload = Any.Pack(new StringValue { Value = "run" }),
            Route = EnvelopeRouteSemantics.CreateDirect("publisher-1", "route-target"),
            Propagation = new EnvelopePropagation { CorrelationId = "existing-correlation" },
        };

        var prepared = await service.PrepareAsync(
            new ScheduledDispatchConfiguration(
                "schedule-1",
                string.Empty,
                new ScheduledDispatchTargetDescriptor(
                    ScheduledDispatchTargetKind.Envelope,
                    Envelope: envelope),
                "0 9 * * *",
                "UTC",
                true,
                new Dictionary<string, string>()),
            "cmd-1",
            "corr-1");

        prepared.TargetActorId.Should().Be("route-target");
        prepared.TriggerEnvelope.Id.Should().Be("existing-command");
        prepared.TriggerEnvelope.Route.PublisherActorId.Should().Be("publisher-1");
        prepared.TriggerEnvelope.Propagation!.CorrelationId.Should().Be("existing-correlation");
        envelope.Id.Should().Be("existing-command");
    }

    [Fact]
    public async Task TargetPreparation_ShouldRejectMissingEnvelopeTargetActorAndUnsupportedKind()
    {
        var service = new ScheduledDispatchTargetPreparationService();
        var missingActor = () => service.PrepareAsync(
            new ScheduledDispatchConfiguration(
                "schedule-1",
                string.Empty,
                new ScheduledDispatchTargetDescriptor(
                    ScheduledDispatchTargetKind.Envelope,
                    Envelope: new EventEnvelope { Payload = Any.Pack(new Empty()) }),
                "0 9 * * *",
                "UTC",
                true,
                new Dictionary<string, string>()),
            "cmd-1",
            "corr-1");
        var unsupported = () => service.PrepareAsync(
            new ScheduledDispatchConfiguration(
                "schedule-1",
                string.Empty,
                new ScheduledDispatchTargetDescriptor((ScheduledDispatchTargetKind)99),
                "0 9 * * *",
                "UTC",
                true,
                new Dictionary<string, string>()),
            "cmd-1",
            "corr-1");

        await missingActor.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*actor id*");
        await unsupported.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Unsupported scheduled dispatch target kind*");
    }

    [Fact]
    public void Calculator_ShouldReportInvalidInputsAndComputeDueTime()
    {
        ScheduledDispatchCalculator.TryGetNextOccurrence(
                string.Empty,
                "UTC",
                DateTimeOffset.UtcNow,
                out _,
                out var missingCronError)
            .Should().BeFalse();
        missingCronError.Should().Be("Cron expression is required.");

        ScheduledDispatchCalculator.TryResolveTimeZone(
                "invalid-zone",
                out _,
                out var timezoneError)
            .Should().BeFalse();
        timezoneError.Should().NotBeNullOrWhiteSpace();

        ScheduledDispatchCalculator.ComputeDueTime(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow)
            .Should().Be(TimeSpan.FromSeconds(1));
        ScheduledDispatchCalculator.NormalizeTimezone(" Asia/Shanghai ").Should().Be("Asia/Shanghai");
    }

    private static ScheduledDispatchApplicationService CreateService() =>
        new(
            new RecordingScheduledDispatchActorPort(),
            new RecordingScheduledDispatchQueryPort(),
            new ScheduledDispatchTargetPreparationService(),
            new NoopScheduledDispatchCredentialAdmissionPort());

    private static ScheduledDispatchConfiguration CreateEnvelopeConfiguration(string scheduleId) =>
        new(
            scheduleId,
            string.Empty,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.Envelope,
                ActorId: "actor-1",
                Envelope: new EventEnvelope { Payload = Any.Pack(new Empty()) }),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>());

    private static ScheduledDispatchConfiguration CreateServiceInvocationConfiguration(
        string scheduleId,
        ScheduledDispatchScheduleKind scheduleKind,
        ScheduledDispatchCredentialRequirementTargetKind credentialRequirementTargetKind,
        Any? payload = null) =>
        new ScheduledDispatchConfiguration(
            scheduleId,
            string.Empty,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    "run",
                    payload ?? Any.Pack(new Empty()))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>(),
            scheduleKind)
        {
            CredentialRequirementTargetKind = credentialRequirementTargetKind,
        };

    private static ScheduledDispatchConfiguration CreateServiceInvocationConfiguration(
        string scheduleId,
        ScheduledServiceInvocationAuth auth) =>
        new(
            scheduleId,
            "Invoke",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                    "run",
                    Any.Pack(new StringValue { Value = "invoke" }),
                    Auth: auth)),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>());

    private static ScheduledDispatchDetail CreateSummaryDetail(
        string scheduleId,
        ScheduledDispatchTargetKind targetKind,
        ScheduledDispatchScheduleKind scheduleKind,
        ScheduledDispatchCredentialRequirementTargetKind credentialRequirementTargetKind,
        ScheduledDispatchCredentialSourceKind credentialSourceKind) =>
        new(
            new ScheduledDispatchSummary(
                scheduleId,
                string.Empty,
                targetKind,
                targetKind == ScheduledDispatchTargetKind.Envelope ? "actor-1" : string.Empty,
                Any.Pack(new Empty()).TypeUrl,
                string.Empty,
                string.Empty,
                targetKind == ScheduledDispatchTargetKind.ServiceInvocation ? "chat" : string.Empty,
                "0 9 * * *",
                "UTC",
                true,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                null,
                null,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                0,
                0,
                new Dictionary<string, string>(),
                $"actor:{scheduleId}",
                ScheduleKind: scheduleKind,
                CredentialRequirementTargetKind: credentialRequirementTargetKind,
                CredentialSourceKind: credentialSourceKind),
            []);

    private static ScheduledDispatchConfiguration CreateScopeOwnerConfiguration(
        string scheduleId = "scope-owner-schedule",
        string tenantId = "scope-1",
        ScheduledServiceInvocationNyxIdSubjectRef? ownerSubject = null) =>
        new(
            scheduleId,
            "Invoke",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity
                    {
                        TenantId = tenantId,
                        AppId = "app",
                        Namespace = "default",
                        ServiceId = "svc",
                    },
                    "run",
                    Any.Pack(new StringValue { Value = "invoke" }),
                    Auth: new ScheduledServiceInvocationAuth(
                        ScopeOwnerNyxId: new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource(
                            "proxy",
                            ownerSubject)))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>());

    private static ScheduledServiceInvocationDurableCredentialReference CreateDurableCredentialReference(
        string credentialId = "credential-1",
        string secretRef = "sec-1",
        string ownerScopeKey = "owner-scope-1",
        string purpose = CredentialSecretPurposes.ScheduledNyxApiKey) =>
        new(
            credentialId,
            new SecretReference
            {
                Ref = secretRef,
                Purpose = purpose,
                OwnerScopeKey = ownerScopeKey,
            });

    private static ScheduledDispatchMutationContext CreateScopeOwnerContext() =>
        new(
            "scope-1",
            new ScheduledServiceInvocationNyxIdSubjectRef(OwnerScope.NyxIdPlatform, string.Empty, "owner-nyx-user"));

    private static Task<ScheduledDispatchMutationReceipt> ExecuteMutationAsync(
        ScheduledDispatchApplicationService service,
        ScheduleMutationKind mutation,
        ScheduledDispatchConfiguration configuration,
        ScheduledDispatchMutationContext context) =>
        mutation switch
        {
            ScheduleMutationKind.Create => service.CreateAsync(configuration, context),
            ScheduleMutationKind.Ensure => service.EnsureAsync(configuration, context),
            ScheduleMutationKind.Update => service.UpdateAsync(configuration.ScheduleId, configuration, context),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
        };

    private static void AssertNoActorMutationDispatch(RecordingScheduledDispatchActorPort actorPort)
    {
        actorPort.ResolvedScheduleIds.Should().BeEmpty();
        actorPort.EnsuredScheduleIds.Should().BeEmpty();
        actorPort.Created.Should().BeEmpty();
        actorPort.Ensured.Should().BeEmpty();
        actorPort.Updated.Should().BeEmpty();
    }

    private static void AssertNoScheduleQuery(RecordingScheduledDispatchQueryPort queryPort)
    {
        queryPort.GetScheduleIds.Should().BeEmpty();
        queryPort.ListRequests.Should().BeEmpty();
        queryPort.FilteredListRequests.Should().BeEmpty();
    }

    private enum ScheduleMutationKind
    {
        Create,
        Ensure,
        Update,
    }

    private static ScheduledInvocationAgentKeyCredentialReference CreateScheduledInvocationAgentKeyReference(
        string purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey)
    {
        var expiresAtUnixMs = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
        return new ScheduledInvocationAgentKeyCredentialReference(
            new SecretReference
            {
                Ref = "sec-schedule",
                Purpose = purpose,
                OwnerScopeKey = "scope-key",
                Fingerprint = "sha256:abc",
                Version = 1,
                CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ExpiresAtUnixMs = expiresAtUnixMs,
            },
            "key-schedule",
            expiresAtUnixMs);
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public Dictionary<string, IActor> ExistingActors { get; } = new(StringComparer.Ordinal);
        public List<string> CreatedIds { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
        {
            ct.ThrowIfCancellationRequested();
            var actor = new RecordingActor(id ?? Guid.NewGuid().ToString("N"));
            CreatedIds.Add(actor.Id);
            ExistingActors[actor.Id] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(ExistingActors.GetValueOrDefault(id));

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(ExistingActors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<EventEnvelope> Envelopes { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Envelopes.Add(envelope.Clone());
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new RecordingAgent(id);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingScheduledDispatchActorPort : IScheduledDispatchActorPort
    {
        public List<string> EnsuredScheduleIds { get; } = [];
        public List<string> ResolvedScheduleIds { get; } = [];
        public HashSet<string> MissingScheduleIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ExistingScheduleIds { get; } = new(StringComparer.Ordinal);
        public bool ResolveUnknownAsMissing { get; init; }
        public List<(string ActorId, ScheduledDispatchConfiguration Configuration, PreparedScheduledDispatchTarget Dispatch)> Created { get; } = [];
        public List<(string ActorId, ScheduledDispatchConfiguration Configuration, PreparedScheduledDispatchTarget Dispatch)> Updated { get; } = [];
        public List<(string ActorId, ScheduledDispatchConfiguration Configuration, PreparedScheduledDispatchTarget Dispatch)> Ensured { get; } = [];
        public List<(string ActorId, string Reason)> Enabled { get; } = [];
        public List<(string ActorId, string Reason)> Disabled { get; } = [];
        public List<(string ActorId, string Reason)> Deleted { get; } = [];
        public List<(string ActorId, DateTimeOffset ScheduledFireAt)> RunNow { get; } = [];

        public Task<string> EnsureScheduleActorAsync(string scheduleId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            EnsuredScheduleIds.Add(scheduleId);
            return Task.FromResult($"actor:{scheduleId}");
        }

        public Task<string?> ResolveScheduleActorAsync(string scheduleId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ResolvedScheduleIds.Add(scheduleId);
            if (MissingScheduleIds.Contains(scheduleId) || ResolveUnknownAsMissing)
                return Task.FromResult<string?>(null);

            return Task.FromResult<string?>($"actor:{scheduleId}");
        }

        public Task<DispatchAdmission> DispatchCreateAsync(
            string actorId,
            ScheduledDispatchConfiguration configuration,
            PreparedScheduledDispatchTarget dispatch,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Created.Add((actorId, configuration, dispatch));
            return Task.FromResult(CreateAdmission(actorId));
        }

        public Task<DispatchAdmission> DispatchUpdateAsync(
            string actorId,
            ScheduledDispatchConfiguration configuration,
            PreparedScheduledDispatchTarget dispatch,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Updated.Add((actorId, configuration, dispatch));
            return Task.FromResult(CreateAdmission(actorId));
        }

        public Task<DispatchAdmission> DispatchEnsureAsync(
            string actorId,
            ScheduledDispatchConfiguration configuration,
            PreparedScheduledDispatchTarget dispatch,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Ensured.Add((actorId, configuration, dispatch));
            return Task.FromResult(CreateAdmission(actorId));
        }

        public Task<DispatchAdmission> DispatchEnableAsync(
            string actorId,
            string reason,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Enabled.Add((actorId, reason));
            return Task.FromResult(CreateAdmission(actorId));
        }

        public Task<DispatchAdmission> DispatchDisableAsync(
            string actorId,
            string reason,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Disabled.Add((actorId, reason));
            return Task.FromResult(CreateAdmission(actorId));
        }

        public Task<DispatchAdmission> DispatchDeleteAsync(
            string actorId,
            string reason,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Deleted.Add((actorId, reason));
            return Task.FromResult(CreateAdmission(actorId));
        }

        public Task<DispatchAdmission> DispatchRunNowAsync(
            string actorId,
            DateTimeOffset scheduledFireAt,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RunNow.Add((actorId, scheduledFireAt));
            return Task.FromResult(CreateAdmission(actorId));
        }

        private static DispatchAdmission CreateAdmission(string actorId) =>
            new(true, "cmd-1", DateTimeOffset.UtcNow, actorId, "corr-1");
    }

    private sealed class RecordingScheduledDispatchDocumentReader : IProjectionDocumentReader<ScheduledDispatchDocument, string>
    {
        public ProjectionDocumentQuery? LastQuery { get; private set; }
        public ProjectionDocumentQueryResult<ScheduledDispatchDocument> Result { get; set; } = new();

        public Task<ScheduledDispatchDocument?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<ScheduledDispatchDocument?>(null);

        public Task<ProjectionDocumentQueryResult<ScheduledDispatchDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingScheduledDispatchQueryPort : IScheduledDispatchQueryPort
    {
        public List<string> GetScheduleIds { get; } = [];
        public List<(int Take, string? Cursor, bool IncludeTotalCount)> ListRequests { get; } = [];
        public List<ScheduledDispatchListQuery> FilteredListRequests { get; } = [];
        public ScheduledDispatchDetail? Detail { get; set; }
        public ScheduledDispatchListResult ListResult { get; init; } =
            new([], null, null);

        public Task<ScheduledDispatchDetail?> GetAsync(string scheduleId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            GetScheduleIds.Add(scheduleId);
            return Task.FromResult(Detail);
        }

        public Task<ScheduledDispatchListResult> ListAsync(
            int take = 50,
            string? cursor = null,
            bool includeTotalCount = false,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ListRequests.Add((take, cursor, includeTotalCount));
            return Task.FromResult(new ScheduledDispatchListResult([], null, includeTotalCount ? 0 : null));
        }

        public Task<ScheduledDispatchListResult> ListAsync(
            ScheduledDispatchListQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            FilteredListRequests.Add(query);
            if (query.IncludeTotalCount && ListResult.TotalCount.HasValue)
                return Task.FromResult(ListResult);
            return Task.FromResult(ListResult with
            {
                TotalCount = query.IncludeTotalCount ? ListResult.TotalCount ?? 0 : null,
            });
        }
    }

    private sealed class RecordingScheduledDispatchCredentialAdmissionPort : IScheduledDispatchCredentialAdmissionPort
    {
        public List<ScheduledDispatchCredentialAdmissionRequest> Requests { get; } = [];

        public ScheduledDispatchCredentialAdmissionResult Result { get; init; } =
            ScheduledDispatchCredentialAdmissionResult.Allowed();

        public Task<ScheduledDispatchCredentialAdmissionResult> AdmitAsync(
            ScheduledDispatchCredentialAdmissionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingScheduledDispatchTargetPreparationService : IScheduledDispatchTargetPreparationService
    {
        private readonly ScheduledDispatchTargetPreparationService _inner = new();

        public List<ScheduledDispatchConfiguration> Requests { get; } = [];

        public Task<PreparedScheduledDispatchTarget> PrepareAsync(
            ScheduledDispatchConfiguration configuration,
            string commandId,
            string correlationId,
            CancellationToken ct = default)
        {
            Requests.Add(configuration);
            return _inner.PrepareAsync(configuration, commandId, correlationId, ct);
        }
    }
}
