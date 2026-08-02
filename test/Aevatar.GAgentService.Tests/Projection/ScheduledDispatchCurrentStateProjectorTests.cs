using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
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
        (await store.GetAsync("scheduled-dispatch:schedule-1")).Should().BeNull();
        document!.ServiceKey.Should().Be(ServiceKeys.Build(identity));
        document.ServiceId.Should().Be("svc");
        document.ServiceEndpointId.Should().Be("chat");
        document.Prompt.Should().Be("run");
        document.ScheduleKind.Should().Be(ScheduledDispatchScheduleKind.Generic.ToString());
        document.StateVersion.Should().Be(9);
        document.LastEventId.Should().Be("evt-9");
    }

    [Theory]
    [InlineData("Connector.Http.Authorization")]
    [InlineData("CONNECTOR.HTTP.AUTHORIZATION")]
    public async Task ProjectAsync_ShouldNotProjectCaseVariantConnectorAuthorizationHeader(
        string authorizationHeader)
    {
        var store = new RecordingDocumentStore<ScheduledDispatchDocument>(x => x.Id);
        var projector = new ScheduledDispatchCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-06-18T00:00:00+00:00")));
        var state = CreateServiceInvocationState(
            "schedule-header-case-variant",
            new ServiceIdentity
            {
                TenantId = "tenant",
                AppId = "app",
                Namespace = "default",
                ServiceId = "svc",
            });
        state.Headers[authorizationHeader] = "redacted";
        state.Headers["trace"] = "kept";

        await projector.ProjectAsync(
            CreateContext("scheduled-dispatch:schedule-header-case-variant"),
            WrapCommitted(
                state,
                version: 10,
                eventId: "evt-header-case-variant",
                observedAt: DateTimeOffset.Parse("2026-06-18T01:15:00+00:00")));

        var document = await store.GetAsync("schedule-header-case-variant");
        document.Should().NotBeNull();
        document!.Headers.Keys.Should().NotContain(key =>
            string.Equals(
                key,
                ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey,
                StringComparison.OrdinalIgnoreCase));
        document.Headers.Should().Contain("trace", "kept");
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
    public async Task ProjectAsync_ShouldMaterializeCredentialRequirementFacts()
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
            },
        };

        await projector.ProjectAsync(
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
    }

    [Fact]
    public async Task ProjectAsync_ShouldNotProjectDurableCredentialReference()
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
            },
        };

        await projector.ProjectAsync(
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
        document.CredentialSourceKind.Should()
            .Be(ScheduledDispatchCredentialSourceKind.DurableCredentialReference.ToString());
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
        document.CredentialSourceKind.Should()
            .Be(ScheduledDispatchCredentialSourceKind.ScheduledInvocationAgentKey.ToString());
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

    [Fact]
    public async Task ProjectAsync_ShouldProjectTeamOwnerAndHealthWithoutCredentialReferences()
    {
        var store = new RecordingDocumentStore<ScheduledDispatchDocument>(x => x.Id);
        var projector = new ScheduledDispatchCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-16T00:00:00+00:00")));
        var state = CreateServiceInvocationState(
            "team-schedule",
            new ServiceIdentity
            {
                TenantId = "scope-alpha",
                AppId = "app",
                Namespace = "default",
                ServiceId = "service-alpha",
            });
        state.TeamAutomationOwner = new TeamMemberAutomationOwnerState
        {
            ScopeId = "scope-alpha",
            MemberId = "member-alpha",
            TeamId = "team-alpha",
        };
        state.TeamAutomationLifecycleStatus = TeamAutomationLifecycleStatusState.RevocationPending;
        state.TeamAutomationOperationId = "operation-alpha";
        state.TeamAutomationPermissionDigest = "digest-alpha";
        state.TeamCredentialGeneration = 3;
        state.LastAuthorizationErrorCode = "vault_revoke_pending";
        state.TeamCredentialExpiresAt = Timestamp.FromDateTimeOffset(
            DateTimeOffset.Parse("2026-08-16T00:00:00+00:00"));
        state.PendingRevocationTeamCredential = new ScheduledInvocationAgentKeyCredentialReferenceState
        {
            ApiKeyId = "api-key-sensitive-id",
            KeyExpiresAtUnixMs = DateTimeOffset.Parse("2026-08-16T00:00:00+00:00")
                .ToUnixTimeMilliseconds(),
            SecretReference = new SecretReference
            {
                Ref = "sec-sensitive-reference",
                Purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
                OwnerScopeKey = "scope-alpha:member-alpha",
            },
        };

        await projector.ProjectAsync(
            CreateContext("scheduled-dispatch:team-schedule"),
            WrapCommitted(
                state,
                version: 17,
                eventId: "evt-team",
                observedAt: DateTimeOffset.Parse("2026-07-16T01:00:00+00:00")));

        var document = await store.GetAsync("team-schedule");
        document.Should().NotBeNull();
        document!.TeamOwned.Should().BeTrue();
        document.TeamAutomationOwner.Should().BeEquivalentTo(new TeamMemberAutomationOwnerDocument
        {
            ScopeId = "scope-alpha",
            MemberId = "member-alpha",
        });
        document.TeamId.Should().Be("team-alpha");
        document.TeamAutomationLifecycleStatus.Should().Be(TeamAutomationLifecycleStatusDocument.RevocationPending);
        document.RevocationPending.Should().BeTrue();
        document.StateVersion.Should().Be(17);
        AssertDocumentDoesNotContain(document, "api-key-sensitive-id");
        AssertDocumentDoesNotContain(document, "sec-sensitive-reference");
    }

    [Fact]
    public async Task ProjectAsync_ShouldExposeNeedsAuthorizationWithStableCredentialFailureCode()
    {
        var store = new RecordingDocumentStore<ScheduledDispatchDocument>(x => x.Id);
        var projector = new ScheduledDispatchCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-16T00:00:00+00:00")));
        var state = CreateServiceInvocationState(
            "team-needs-authorization",
            new ServiceIdentity
            {
                TenantId = "scope-alpha",
                AppId = "app",
                Namespace = "default",
                ServiceId = "service-alpha",
            });
        state.TeamAutomationOwner = new TeamMemberAutomationOwnerState
        {
            ScopeId = "scope-alpha",
            MemberId = "member-alpha",
        };
        state.TeamAutomationLifecycleStatus = TeamAutomationLifecycleStatusState.NeedsAuthorization;
        state.LastAuthorizationErrorCode = "credential_unresolvable";

        await projector.ProjectAsync(
            CreateContext("scheduled-dispatch:team-needs-authorization"),
            WrapCommitted(
                state,
                version: 18,
                eventId: "evt-needs-authorization",
                observedAt: DateTimeOffset.Parse("2026-07-16T02:00:00+00:00")));

        var document = await store.GetAsync("team-needs-authorization");
        document.Should().NotBeNull();
        document!.TeamAutomationLifecycleStatus.Should()
            .Be(TeamAutomationLifecycleStatusDocument.NeedsAuthorization);
        document.LastAuthorizationErrorCode.Should().Be("credential_unresolvable");
        document.StateVersion.Should().Be(18);
    }

    [Fact]
    public async Task ProjectAsync_ShouldExposePersistedOwnerLLMRuntimeEvidenceFromActiveAuthorizationFact()
    {
        var store = new RecordingDocumentStore<ScheduledDispatchDocument>(x => x.Id);
        var projector = new ScheduledDispatchCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-24T00:00:00+00:00")));
        var state = CreateServiceInvocationState(
            "team-owner-llm",
            new ServiceIdentity
            {
                TenantId = "scope-alpha",
                AppId = "app",
                Namespace = "default",
                ServiceId = "service-alpha",
            });
        state.Target.ServiceInvocation.AuthorizationFact = new ScheduledInvocationAuthorizationFactState
        {
            OwnerLlmSelection = new ScheduledInvocationOwnerLLMSelection
            {
                RouteKind = LLMRouteKind.Gateway,
                RouteValue = "/api/v1/llm/gateway/v1",
                Model = "fallback-model",
            },
        };
        state.ActiveTeamAuthorizationFact = new ScheduledInvocationAuthorizationFactState
        {
            Owner = new ScheduledInvocationAuthorizationOwnerState
            {
                Authority = "caller-authority-sensitive",
                OwnerKind = "Personal",
                OwnerSubject = "caller-subject-sensitive",
            },
            OwnerLlmSelection = new ScheduledInvocationOwnerLLMSelection
            {
                RouteKind = LLMRouteKind.NyxIdUserService,
                RouteValue = "/api/v1/proxy/s/chrono-llm-public",
                NyxIdUserServiceId = "us-chrono",
                ServiceSlugSnapshot = "chrono-llm-public",
                Model = "gpt-5.5",
            },
        };

        await projector.ProjectAsync(
            CreateContext("scheduled-dispatch:team-owner-llm"),
            WrapCommitted(
                state,
                version: 23,
                eventId: "evt-owner-llm",
                observedAt: DateTimeOffset.Parse("2026-07-24T01:00:00+00:00")));

        var document = await store.GetAsync("team-owner-llm");
        document.Should().NotBeNull();
        ReadRequiredStringProperty(document!, "OwnerLlmRouteKind").Should().Be("nyx_id_user_service");
        ReadRequiredStringProperty(document, "OwnerLlmRoute").Should()
            .Be("/api/v1/proxy/s/chrono-llm-public");
        ReadRequiredStringProperty(document, "OwnerLlmUserServiceId").Should().Be("us-chrono");
        ReadRequiredStringProperty(document, "OwnerLlmServiceSlug").Should().Be("chrono-llm-public");
        ReadRequiredStringProperty(document, "OwnerLlmModel").Should().Be("gpt-5.5");
        document.StateVersion.Should().Be(23);
        AssertDocumentDoesNotContain(document, "caller-authority-sensitive");
        AssertDocumentDoesNotContain(document, "caller-subject-sensitive");
        AssertDocumentDoesNotContain(document, "fallback-model");
    }

    [Theory]
    [InlineData(LLMRouteKind.Unspecified, "unspecified", "")]
    [InlineData(LLMRouteKind.Gateway, "gateway", "/api/v1/llm/gateway/v1")]
    public async Task ProjectAsync_ShouldExposeExplicitRouteKindFromTargetAuthorizationFact(
        LLMRouteKind routeKind,
        string expectedRouteKind,
        string route)
    {
        var store = new RecordingDocumentStore<ScheduledDispatchDocument>(x => x.Id);
        var projector = new ScheduledDispatchCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-24T00:00:00+00:00")));
        var state = CreateServiceInvocationState(
            "target-owner-llm",
            new ServiceIdentity
            {
                TenantId = "scope-alpha",
                AppId = "app",
                Namespace = "default",
                ServiceId = "service-alpha",
            });
        state.Target.ServiceInvocation.AuthorizationFact = new ScheduledInvocationAuthorizationFactState
        {
            OwnerLlmSelection = new ScheduledInvocationOwnerLLMSelection
            {
                RouteKind = routeKind,
                RouteValue = route,
                Model = routeKind == LLMRouteKind.Gateway ? "gpt-5.5" : string.Empty,
            },
        };

        await projector.ProjectAsync(
            CreateContext("scheduled-dispatch:target-owner-llm"),
            WrapCommitted(
                state,
                version: 24,
                eventId: $"evt-owner-llm-{expectedRouteKind}",
                observedAt: DateTimeOffset.Parse("2026-07-24T02:00:00+00:00")));

        var document = await store.GetAsync("target-owner-llm");
        document.Should().NotBeNull();
        ReadRequiredStringProperty(document!, "OwnerLlmRouteKind").Should().Be(expectedRouteKind);
        ReadRequiredStringProperty(document, "OwnerLlmRoute").Should().Be(route);
        document.StateVersion.Should().Be(24);
    }

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeTypedScheduleFailureEvidence()
    {
        var store = new RecordingDocumentStore<ScheduledDispatchDocument>(x => x.Id);
        var projector = new ScheduledDispatchCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-02T00:00:00+00:00")));
        var state = CreateServiceInvocationState(
            "schedule-admission-failure",
            new ServiceIdentity
            {
                TenantId = "scope-alpha",
                AppId = "app-alpha",
                Namespace = "default",
                ServiceId = "svc-alpha",
            });
        state.LastError = "Workflow definition is invalid.";
        state.LastErrorCode = "WORKFLOW_DEFINITION_INVALID";
        state.FireCount = 1;
        state.FailureCount = 1;
        state.FireRecords["fire-alpha"] = new ScheduledDispatchFireRecordState
        {
            IdempotencyKey = "fire-alpha",
            ScheduledFireAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-02T01:00:00+00:00")),
            CompletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-02T01:00:01+00:00")),
            Error = "Workflow definition is invalid.",
            ErrorCode = "WORKFLOW_DEFINITION_INVALID",
            Status = ScheduledDispatchFireStatusState.Failed,
        };

        await projector.ProjectAsync(
            CreateContext("scheduled-dispatch:schedule-admission-failure"),
            WrapCommitted(
                state,
                version: 25,
                eventId: "evt-admission-failure",
                observedAt: DateTimeOffset.Parse("2026-08-02T01:00:02+00:00")));

        var document = await store.GetAsync("schedule-admission-failure");
        document.Should().NotBeNull();
        document!.LastError.Should().Be("Workflow definition is invalid.");
        document.LastErrorCode.Should().Be("WORKFLOW_DEFINITION_INVALID");
        var fire = document.FireRecords.Should().ContainSingle().Subject;
        fire.Error.Should().Be("Workflow definition is invalid.");
        fire.ErrorCode.Should().Be("WORKFLOW_DEFINITION_INVALID");
        fire.TargetActorId.Should().BeEmpty();
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

    private static string ReadRequiredStringProperty(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        property.Should().NotBeNull($"{propertyName} is part of the runtime evidence contract");
        return property!.GetValue(value).Should().BeOfType<string>().Which;
    }
}
