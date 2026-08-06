using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Application.Schedules;
using Aevatar.GAgentService.Hosting.Endpoints.Schedules;
using Aevatar.GAgentService.Infrastructure.Schedules;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScheduledDispatchServiceInvocationTests
{
    private const string OwnerLLMRoute = "/api/v1/proxy/s/chrono-llm-public";
    private const string OwnerLLMModel = "gpt-5.5";
    private const string OwnerLLMServiceId = "nyx-llm-service-alpha";

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
        request.ScheduleId.Should().Be("schedule-1");
        request.Payload.Unpack<StringValue>().Value.Should().Be("run");
    }

    [Fact]
    public async Task PrepareAsync_ShouldRejectRawEnvelopeTarget()
    {
        var service = new ScheduledDispatchTargetPreparationService();
        var rawEnvelopeConfiguration = new ScheduledDispatchConfiguration(
            "schedule-raw-envelope",
            string.Empty,
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.Envelope,
                ActorId: "actor-cross-owner",
                Envelope: new EventEnvelope { Payload = Any.Pack(new Empty()) }),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>());

        var act = () => service.PrepareAsync(
            rawEnvelopeConfiguration,
            "cmd-raw-envelope",
            "corr-raw-envelope");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*raw envelope*not supported*");
    }

    [Fact]
    public async Task PrepareAsync_ShouldPreserveDurableLlmControlAndStripCredentials()
    {
        var service = new ScheduledDispatchTargetPreparationService();
        var configuration = new ScheduledDispatchConfiguration(
            "schedule-llm",
            "LLM",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { ServiceId = "svc" },
                    "chat",
                    Any.Pack(new ChatRequestEvent
                    {
                        Prompt = "hello",
                        ConnectorHttpAuthorization = "Bearer stored-connector-token",
                        CallerSourceReadableNyxIdBearerToken = "source-readable-secret",
                        Headers =
                        {
                            [ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey] = "Bearer header-token",
                            ["client"] = "kept",
                        },
                        Metadata =
                        {
                            [ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey] = "Bearer metadata-token",
                            ["trace"] = "kept",
                        },
                        CallerDurableCredential = new DurableCallerCredentialRef
                        {
                            Ref = "forged",
                            Purpose = CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
                        },
                        ToolContext = new AgentToolExecutionContextPayload
                        {
                            Credentials = new AgentToolCredentialsPayload
                            {
                                NyxIdAccessToken = "tool-owner-secret",
                                NyxIdOrgToken = "tool-org-secret",
                                SenderNyxIdAccessToken = "tool-sender-secret",
                                SourceReadableNyxIdAccessToken = "tool-source-secret",
                            },
                            Routing = new LLMRequestRoutingContextPayload
                            {
                                ModelOverride = "tool-model",
                                NyxIdRoutePreference = "tool-route",
                                MaxToolRoundsOverride = 5,
                                UserMemoryPrompt = "tool memory",
                            },
                        },
                        LlmControl = new LLMControlContextPayload
                        {
                            NyxIdAccessToken = "owner-secret",
                            NyxIdOrgToken = "org-secret",
                            SenderNyxIdAccessToken = "sender-secret",
                            ModelOverride = "sonnet",
                            NyxIdRoutePreference = "low-latency",
                            MaxToolRoundsOverride = 3,
                            UserMemoryPrompt = "remember preferences",
                        },
                    }))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>());

        var prepared = await service.PrepareAsync(configuration, "cmd-llm", "corr-llm");

        var request = prepared.TriggerEnvelope.Payload.Unpack<ServiceInvocationRequest>();
        var persistedChat = request.Payload.Unpack<ChatRequestEvent>();
        persistedChat.ConnectorHttpAuthorization.Should().BeEmpty();
        persistedChat.CallerSourceReadableNyxIdBearerToken.Should().BeEmpty();
        persistedChat.Headers.Should().NotContainKey(ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey);
        persistedChat.Headers.Should().Contain("client", "kept");
        persistedChat.Metadata.Should().NotContainKey(ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey);
        persistedChat.Metadata.Should().Contain("trace", "kept");
        persistedChat.LlmControl.Should().NotBeNull();
        persistedChat.LlmControl.NyxIdAccessToken.Should().BeEmpty();
        persistedChat.LlmControl.NyxIdOrgToken.Should().BeEmpty();
        persistedChat.LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
        persistedChat.ConnectorHttpAuthorization.Should().BeEmpty();
        persistedChat.CallerDurableCredential.Should().BeNull();
        persistedChat.LlmControl.ModelOverride.Should().Be("sonnet");
        persistedChat.LlmControl.NyxIdRoutePreference.Should().Be("low-latency");
        persistedChat.LlmControl.MaxToolRoundsOverride.Should().Be(3);
        persistedChat.LlmControl.UserMemoryPrompt.Should().Be("remember preferences");
        persistedChat.ToolContext.Credentials.NyxIdAccessToken.Should().BeEmpty();
        persistedChat.ToolContext.Credentials.NyxIdOrgToken.Should().BeEmpty();
        persistedChat.ToolContext.Credentials.SenderNyxIdAccessToken.Should().BeEmpty();
        persistedChat.ToolContext.Credentials.SourceReadableNyxIdAccessToken.Should().BeEmpty();
        persistedChat.ToolContext.Routing.ModelOverride.Should().Be("tool-model");
        persistedChat.ToolContext.Routing.NyxIdRoutePreference.Should().Be("tool-route");
        persistedChat.ToolContext.Routing.MaxToolRoundsOverride.Should().Be(5);
        persistedChat.ToolContext.Routing.UserMemoryPrompt.Should().Be("tool memory");
        var descriptorChat = prepared.Descriptor.ServiceInvocation!.Payload.Unpack<ChatRequestEvent>();
        descriptorChat.ConnectorHttpAuthorization.Should().BeEmpty();
        descriptorChat.CallerSourceReadableNyxIdBearerToken.Should().BeEmpty();
        descriptorChat.Headers.Should().NotContainKey(ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey);
        descriptorChat.Metadata.Should().NotContainKey(ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey);
        descriptorChat.LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
        descriptorChat.ConnectorHttpAuthorization.Should().BeEmpty();
        descriptorChat.CallerDurableCredential.Should().BeNull();
        descriptorChat.LlmControl.ModelOverride.Should().Be("sonnet");
        descriptorChat.ToolContext.Credentials.SenderNyxIdAccessToken.Should().BeEmpty();
        descriptorChat.ToolContext.Credentials.SourceReadableNyxIdAccessToken.Should().BeEmpty();
        descriptorChat.ToolContext.Routing.ModelOverride.Should().Be("tool-model");
    }

    [Theory]
    [InlineData("Connector.Http.Authorization")]
    [InlineData("CONNECTOR.HTTP.AUTHORIZATION")]
    public async Task PrepareAsync_ShouldStripCaseVariantConnectorAuthorizationKeys(string authorizationKey)
    {
        var service = new ScheduledDispatchTargetPreparationService();
        var payload = new ChatRequestEvent { Prompt = "hello" };
        payload.Headers[authorizationKey] = "redacted";
        payload.Headers["trace"] = "kept";
        payload.Metadata[authorizationKey] = "redacted";
        payload.Metadata["annotation"] = "kept";
        var configuration = new ScheduledDispatchConfiguration(
            "schedule-case-variant",
            "Case variant",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { ServiceId = "svc" },
                    "chat",
                    Any.Pack(payload))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>());

        var prepared = await service.PrepareAsync(configuration, "cmd-case-variant", "corr-case-variant");

        var preparedChat = prepared.Descriptor.ServiceInvocation!.Payload.Unpack<ChatRequestEvent>();
        preparedChat.Headers.Keys.Should().NotContain(key =>
            string.Equals(key, authorizationKey, StringComparison.OrdinalIgnoreCase));
        preparedChat.Metadata.Keys.Should().NotContain(key =>
            string.Equals(key, authorizationKey, StringComparison.OrdinalIgnoreCase));
        preparedChat.Headers.Should().Contain("trace", "kept");
        preparedChat.Metadata.Should().Contain("annotation", "kept");
    }

    [Fact]
    public async Task PrepareAsync_ShouldStripToolContextCredentialsWhenLlmControlIsMissing()
    {
        var service = new ScheduledDispatchTargetPreparationService();
        var configuration = new ScheduledDispatchConfiguration(
            "schedule-tool-context",
            "Tool context",
            new ScheduledDispatchTargetDescriptor(
                ScheduledDispatchTargetKind.ServiceInvocation,
                ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                    new ServiceIdentity { ServiceId = "svc" },
                    "chat",
                    Any.Pack(new ChatRequestEvent
                    {
                        Prompt = "hello",
                        ToolContext = new AgentToolExecutionContextPayload
                        {
                            Credentials = new AgentToolCredentialsPayload
                            {
                                NyxIdAccessToken = "owner-secret",
                                NyxIdOrgToken = "org-secret",
                                SenderNyxIdAccessToken = "sender-secret",
                            },
                            Routing = new LLMRequestRoutingContextPayload
                            {
                                ModelOverride = "opus",
                            },
                        },
                    }))),
            "0 9 * * *",
            "UTC",
            true,
            new Dictionary<string, string>());

        var prepared = await service.PrepareAsync(configuration, "cmd-tool", "corr-tool");

        var request = prepared.TriggerEnvelope.Payload.Unpack<ServiceInvocationRequest>();
        var persistedChat = request.Payload.Unpack<ChatRequestEvent>();
        persistedChat.LlmControl.Should().BeNull();
        persistedChat.ToolContext.Credentials.NyxIdAccessToken.Should().BeEmpty();
        persistedChat.ToolContext.Credentials.NyxIdOrgToken.Should().BeEmpty();
        persistedChat.ToolContext.Credentials.SenderNyxIdAccessToken.Should().BeEmpty();
        persistedChat.ToolContext.Routing.ModelOverride.Should().Be("opus");
    }

    [Fact]
    public void ScheduledDispatchServiceInvocationHttpRequest_ShouldRejectExternalCallerDurableCredential()
    {
        var serviceTarget = new ScheduledDispatchServiceInvocationTargetHttpRequest
        {
            Identity = new ServiceIdentity { TenantId = "tenant", ServiceId = "svc" },
            EndpointId = "chat",
            PayloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
        };

        FluentActions.Invoking(() => serviceTarget.ToTarget(
                Any.Pack(new ChatRequestEvent
                {
                    Prompt = "hello",
                    CallerDurableCredential = CreateDurableCallerCredentialRef(),
                }),
                "rev-1"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*caller_durable_credential*trusted-only*");
        FluentActions.Invoking(() => serviceTarget.ToTarget(
                Any.Pack(new ChatRequestEvent
                {
                    Prompt = "hello",
                    CallerDurableCredential = new DurableCallerCredentialRef(),
                }),
                "rev-1"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*caller_durable_credential*trusted-only*");
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_ShouldInvokeExplicitServiceInvocationPort()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort();
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange);

        var receipt = await port.DispatchAsync(
            new ScheduledServiceInvocationDispatchRequest(
                new ServiceInvocationRequest
                {
                    CommandId = "cmd-invoke",
                    CorrelationId = "corr-invoke",
                    Payload = Any.Pack(new StringValue { Value = "invoke" }),
                },
                ScheduleId: "schedule-invoke"));

        var invokedRequest = invocationPort.Requests.Should().ContainSingle().Which;
        invokedRequest.Payload.Unpack<StringValue>().Value.Should().Be("invoke");
        invokedRequest.ScheduleId.Should().Be("schedule-invoke");
        credentialExchange.Sources.Should().BeEmpty();
        receipt.Accepted.Should().BeTrue();
        receipt.CommandId.Should().Be("cmd-invoke");
        receipt.CorrelationId.Should().Be("corr-invoke");
        receipt.TargetActorId.Should().Be("service-actor");
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithWorkflowAdmissionFailure_ShouldExposeSafeScheduleFailure()
    {
        var workflowFailure = new WorkflowExternalCapabilityAdmissionException(
            new ExternalCapabilityReadiness
            {
                Status = ExternalCapabilityReadinessStatus.ContractDrift,
                Blockers =
                {
                    new ExternalCapabilityBlocker
                    {
                        Status = ExternalCapabilityReadinessStatus.ContractDrift,
                        Code = "CAPABILITY_ADMISSION_REBIND_REQUIRED",
                        SafeMessage = "Workflow capability binding must be refreshed.",
                    },
                },
            });
        var invocationPort = new RecordingServiceInvocationPort(workflowFailure);
        var port = new ScheduledServiceInvocationDispatchPort(
            invocationPort,
            new RecordingScheduledServiceInvocationCredentialExchangePort());

        var act = () => port.DispatchAsync(
            new ScheduledServiceInvocationDispatchRequest(
                new ServiceInvocationRequest
                {
                    CommandId = "cmd-admission",
                    CorrelationId = "corr-admission",
                    Payload = Any.Pack(new StringValue { Value = "invoke" }),
                },
                ScheduleId: "schedule-admission"));

        var failure = await act.Should().ThrowAsync<ScheduledWorkflowAdmissionException>();
        failure.Which.StableCode.Should().Be("CAPABILITY_ADMISSION_REBIND_REQUIRED");
        failure.Which.SafeMessage.Should().Be("Workflow capability binding must be refreshed.");
        invocationPort.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithValidExactOwnerLLMSelection_ShouldInvokeServiceUnchanged()
    {
        var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort();
        var vault = new RecordingSecretVault("must-not-resolve");
        var port = new ScheduledServiceInvocationDispatchPort(
            invocationPort,
            credentialExchange,
            vault,
            timeProvider: new FixedTimeProvider(now));

        await port.DispatchAsync(CreateAuthorizationFactDispatch(now));

        var invoked = invocationPort.Requests.Should().ContainSingle().Which;
        invoked.ScheduleId.Should().Be("schedule-authorized");
        var chat = invoked.Payload.Unpack<ChatRequestEvent>();
        chat.LlmControl.NyxIdRoutePreference.Should().Be(OwnerLLMRoute);
        chat.LlmControl.ModelOverride.Should().Be(OwnerLLMModel);
        credentialExchange.Sources.Should().BeEmpty();
        vault.ResolveRequests.Should().BeEmpty();
        vault.StoreRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithNoServiceGrants_ShouldNotRequireCatalogAuthority()
    {
        var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        var invocationPort = new RecordingServiceInvocationPort();
        var port = new ScheduledServiceInvocationDispatchPort(
            invocationPort,
            new RecordingScheduledServiceInvocationCredentialExchangePort(),
            secretVault: null,
            timeProvider: new FixedTimeProvider(now));
        var dispatch = new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                CommandId = "cmd-no-service-grants",
                CorrelationId = "corr-no-service-grants",
                Identity = new ServiceIdentity { ServiceId = "svc-alpha" },
                EndpointId = "chat",
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "run" }),
            },
            CreateScheduledAgentKeyAuth(now.AddHours(1)),
            ProjectNyxIdAccessTokenToWorkflowCallerCredential: true,
            ScheduleId: "schedule-no-service-grants",
            AuthorizationFact: CreateAuthorizationFactDispatch(now).AuthorizationFact! with
            {
                ServiceGrants = [],
                ServiceGrantsNotRequired = true,
                Authority = new ScheduledInvocationAuthorizationAuthority(
                    11,
                    12,
                    13,
                    0,
                    0,
                    default,
                    default,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    default),
                OwnerLLMSelection = null,
            });

        await port.DispatchAsync(dispatch);

        invocationPort.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithExpiredCatalogFreshness_ShouldUseCommittedFact()
    {
        var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        var invocationPort = new RecordingServiceInvocationPort();
        var port = new ScheduledServiceInvocationDispatchPort(
            invocationPort,
            new RecordingScheduledServiceInvocationCredentialExchangePort(),
            secretVault: null,
            timeProvider: new FixedTimeProvider(now));
        var dispatch = CreateAuthorizationFactDispatch(now);
        dispatch = dispatch with
        {
            AuthorizationFact = dispatch.AuthorizationFact! with
            {
                Authority = dispatch.AuthorizationFact.Authority with
                {
                    CatalogFreshUntil = now.AddMinutes(-1),
                },
            },
        };

        await port.DispatchAsync(dispatch);

        invocationPort.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithAgentKeyWithoutAuthorizationFact_ShouldRejectBeforeCredentialAccess()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("unused");
        var vault = new RecordingSecretVault("agent-key-token");
        var expiresAtUnixMs = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeMilliseconds();
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange, vault);
        var dispatch = new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                CommandId = "cmd-missing-authorization-fact",
                CorrelationId = "corr-missing-authorization-fact",
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
            },
            new ScheduledServiceInvocationAuth(new ScheduledInvocationAgentKeyCredentialReference(
                new SecretReference
                {
                    Ref = "sec-agent-key",
                    Purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
                    OwnerScopeKey = "scope-key",
                    ExpiresAtUnixMs = expiresAtUnixMs,
                },
                "key-schedule",
                expiresAtUnixMs)));

        var act = () => port.DispatchAsync(dispatch);

        var failure = await act.Should().ThrowAsync<ScheduledServiceInvocationAuthorizationException>();
        failure.Which.Code.Should()
            .Be(ScheduledServiceInvocationAuthorizationFailureCode.AuthorizationFactInvalid);
        invocationPort.Requests.Should().BeEmpty();
        credentialExchange.Sources.Should().BeEmpty();
        vault.ResolveRequests.Should().BeEmpty();
        vault.StoreRequests.Should().BeEmpty();
    }

    [Fact]
    public void ScheduledInvocationAuthorizationFact_ShouldExposeOnlyPerServiceNodeGrants()
    {
        typeof(ScheduledInvocationAuthorizationFact).GetProperties()
            .Should().NotContain(property => property.Name == "NodeGrants");
        typeof(ScheduledInvocationAuthorizationFact).Assembly
            .GetType("Aevatar.GAgentService.Abstractions.Schedules.ScheduledInvocationAuthorizationNodeGrant")
            .Should().BeNull();

        var authorityProperties = typeof(ScheduledInvocationAuthorizationAuthority)
            .GetProperties()
            .Select(property => property.Name);
        authorityProperties.Should().NotContain("CatalogExternalRevision");
        authorityProperties.Should().Contain([
            "CatalogContentDigest",
            "CatalogContractVersion",
            "CatalogPolicyVersion",
            "CatalogEvaluatedAt",
        ]);
    }

    [Theory]
    [MemberData(nameof(InvalidAuthorizationFactCases))]
    public async Task ScheduledServiceInvocationDispatchPort_WithInvalidAuthorizationFact_ShouldRejectBeforeInvocation(
        string _,
        Func<ScheduledServiceInvocationDispatchRequest, ScheduledServiceInvocationDispatchRequest> invalidate)
    {
        var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        var invocationPort = new RecordingServiceInvocationPort();
        var port = new ScheduledServiceInvocationDispatchPort(
            invocationPort,
            new RecordingScheduledServiceInvocationCredentialExchangePort(),
            secretVault: null,
            timeProvider: new FixedTimeProvider(now));
        var dispatch = invalidate(CreateAuthorizationFactDispatch(now));

        var act = () => port.DispatchAsync(dispatch);

        await act.Should().ThrowAsync<InvalidOperationException>();
        invocationPort.Requests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(InvalidOwnerLLMSelectionCases))]
    public async Task ScheduledServiceInvocationDispatchPort_WithInvalidRequiredOwnerLLMSelection_ShouldRejectBeforeCredentialAccess(
        string _,
        Func<ScheduledServiceInvocationDispatchRequest, ScheduledServiceInvocationDispatchRequest> invalidate)
    {
        var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort();
        var vault = new RecordingSecretVault("must-not-resolve");
        var port = new ScheduledServiceInvocationDispatchPort(
            invocationPort,
            credentialExchange,
            vault,
            timeProvider: new FixedTimeProvider(now));
        var dispatch = invalidate(CreateAuthorizationFactDispatch(now));

        var act = () => port.DispatchAsync(dispatch);

        var failure = await act.Should().ThrowAsync<ScheduledServiceInvocationAuthorizationException>();
        failure.Which.Code.Should().Be(ScheduledServiceInvocationAuthorizationFailureCode.OwnerLLMSelectionInvalid);
        failure.Which.StableCode.Should().Be("owner_llm_selection_invalid");
        invocationPort.Requests.Should().BeEmpty();
        credentialExchange.Sources.Should().BeEmpty();
        vault.ResolveRequests.Should().BeEmpty();
        vault.StoreRequests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(OwnerLLMPayloadMismatchCases))]
    public async Task ScheduledServiceInvocationDispatchPort_WithOwnerLLMPayloadFactDrift_ShouldRejectBeforeCredentialAccess(
        string _,
        Func<ScheduledServiceInvocationDispatchRequest, ScheduledServiceInvocationDispatchRequest> mutate)
    {
        var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort();
        var vault = new RecordingSecretVault("must-not-resolve");
        var port = new ScheduledServiceInvocationDispatchPort(
            invocationPort,
            credentialExchange,
            vault,
            timeProvider: new FixedTimeProvider(now));
        var dispatch = mutate(CreateAuthorizationFactDispatch(now));

        var act = () => port.DispatchAsync(dispatch);

        var failure = await act.Should().ThrowAsync<ScheduledServiceInvocationAuthorizationException>();
        failure.Which.Code.Should().Be(ScheduledServiceInvocationAuthorizationFailureCode.OwnerLLMPayloadMismatch);
        failure.Which.StableCode.Should().Be("owner_llm_payload_mismatch");
        invocationPort.Requests.Should().BeEmpty();
        credentialExchange.Sources.Should().BeEmpty();
        vault.ResolveRequests.Should().BeEmpty();
        vault.StoreRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithoutAuth_ShouldClearStoredConnectorAuthorization()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort();
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange);
        var original = new ServiceInvocationRequest
        {
            CommandId = "cmd-invoke",
            CorrelationId = "corr-invoke",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                ConnectorHttpAuthorization = "Bearer stored-token",
                Headers =
                {
                    [ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey] = "Bearer header-token",
                },
                Metadata =
                {
                    [ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey] = "Bearer metadata-token",
                    ["trace"] = "kept",
                },
            }),
        };

        await port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(original));

        var invokedChat = invocationPort.Requests.Should().ContainSingle().Which.Payload.Unpack<ChatRequestEvent>();
        invokedChat.ConnectorHttpAuthorization.Should().BeEmpty();
        invokedChat.Headers.Should().NotContainKey(ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey);
        invokedChat.Metadata.Should().NotContainKey(ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey);
        invokedChat.Metadata.Should().Contain("trace", "kept");
        original.Payload.Unpack<ChatRequestEvent>().ConnectorHttpAuthorization.Should().Be("Bearer stored-token");
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithScopeOwnerAuth_ShouldInjectOwnerTokenIntoOwnerLlmControlFields()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("owner-token");
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange);
        var original = new ServiceInvocationRequest
        {
            CommandId = "cmd-invoke",
            CorrelationId = "corr-invoke",
            Identity = new ServiceIdentity { TenantId = "owner-nyx-user", ServiceId = "svc" },
            Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
        };
        var auth = new ScheduledServiceInvocationAuth(
            ScopeOwnerNyxId: new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource("proxy"));

        await port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(original, auth));

        var exchangedSource = credentialExchange.Sources.Should().ContainSingle().Which;
        exchangedSource.Role.Should().Be(ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner);
        exchangedSource.Scope.Should().Be("proxy");
        var invokedChat = invocationPort.Requests.Should().ContainSingle().Which.Payload.Unpack<ChatRequestEvent>();
        invokedChat.LlmControl.NyxIdAccessToken.Should().Be("owner-token");
        invokedChat.LlmControl.NyxIdOrgToken.Should().Be("owner-token");
        invokedChat.LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
        invokedChat.ConnectorHttpAuthorization.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithWorkflowProjection_ShouldCarryAuthorityWithoutVaultOrExchange()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("owner-token");
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange);
        var auth = new ScheduledServiceInvocationAuth(
            ScopeOwnerNyxId: new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource(
                "proxy",
                new ScheduledServiceInvocationNyxIdSubjectRef("nyxid", "tenant-1", "owner-1")))
        {
            CallerAuthority = new ScheduledCallerNyxIdAuthority
            {
                Platform = "nyxid",
                Tenant = "tenant-1",
                ExternalUserId = "owner-1",
                Scope = "proxy",
                BindingId = "bnd-owner-alpha",
            },
        };
        var original = new ServiceInvocationRequest
        {
            CommandId = "cmd-invoke",
            CorrelationId = "corr-invoke",
            Identity = new ServiceIdentity { TenantId = "owner-nyx-user", ServiceId = "svc" },
            Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
        };

        await port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            original,
            auth,
            ProjectNyxIdAccessTokenToWorkflowCallerCredential: true,
            ScheduleId: "schedule-owner"));

        credentialExchange.Sources.Should().BeEmpty();
        var chat = invocationPort.Requests.Should().ContainSingle().Which.Payload.Unpack<ChatRequestEvent>();
        chat.CallerDurableCredential.Ref.Should().BeEmpty();
        chat.CallerDurableCredential.ScheduledCallerNyxIdAuthority.ExternalUserId.Should().Be("owner-1");
        chat.CallerDurableCredential.ScheduledCallerNyxIdAuthority.Scope.Should().Be("proxy");
        chat.CallerDurableCredential.ScheduledCallerNyxIdAuthority.BindingId.Should().Be("bnd-owner-alpha");
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithAuthAndWorkflowProjection_ShouldCarrySenderAuthority()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("sender-token-1");
        var vault = new RecordingSecretVault(null);
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange, vault);
        var original = new ServiceInvocationRequest
        {
            CommandId = "cmd-invoke",
            CorrelationId = "corr-invoke",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                ConnectorHttpAuthorization = "Bearer stored-token",
                Metadata =
                {
                    ["trace"] = "kept",
                },
                LlmControl = new LLMControlContextPayload
                {
                    ModelOverride = "sonnet",
                },
            }),
        };
        var auth = new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
            new ScheduledServiceInvocationNyxIdSubjectRef("lark", "tenant-1", "ou-user-1"),
            "proxy"))
        {
            CallerAuthority = new ScheduledCallerNyxIdAuthority
            {
                Platform = "lark",
                Tenant = "tenant-1",
                ExternalUserId = "ou-user-1",
                Scope = "proxy",
                BindingId = "bnd-owner-alpha",
            },
        };

        await port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            original,
            auth,
            new Dictionary<string, string>
            {
                ["connector.http.authorization"] = "Bearer header-token",
                ["schedule"] = "scheduled",
            },
            ProjectNyxIdAccessTokenToWorkflowCallerCredential: true,
            ScheduleId: "schedule-sender"));

        credentialExchange.Sources.Should().BeEmpty();
        var invoked = invocationPort.Requests.Should().ContainSingle().Which;
        invoked.Should().NotBeSameAs(original);
        var invokedChat = invoked.Payload.Unpack<ChatRequestEvent>();
        invokedChat.LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
        invokedChat.LlmControl.ModelOverride.Should().Be("sonnet");
        invokedChat.ConnectorHttpAuthorization.Should().BeEmpty();
        invokedChat.CallerDurableCredential.Should().NotBeNull();
        invokedChat.CallerDurableCredential.Ref.Should().BeEmpty();
        invokedChat.CallerDurableCredential.ScheduledCallerNyxIdAuthority.Should().BeEquivalentTo(
            new ScheduledCallerNyxIdAuthority
            {
                Platform = "lark",
                Tenant = "tenant-1",
                ExternalUserId = "ou-user-1",
                Scope = "proxy",
                BindingId = "bnd-owner-alpha",
            });
        invokedChat.Metadata.Should().Contain("trace", "kept");
        invokedChat.Metadata.Should().NotContainKey("connector.http.authorization");
        invokedChat.Metadata.Should().Contain("schedule", "scheduled");
        invokedChat.Metadata.Should().NotContainValue("sender-token-1");
        invokedChat.Metadata.Should().NotContainValue("Bearer sender-token-1");
        vault.StoreRequests.Should().BeEmpty();
        var originalChat = original.Payload.Unpack<ChatRequestEvent>();
        originalChat.LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
        originalChat.LlmControl.ModelOverride.Should().Be("sonnet");
        originalChat.ConnectorHttpAuthorization.Should().Be("Bearer stored-token");
        originalChat.Metadata.Should().Contain("trace", "kept");
        originalChat.Metadata.Should().NotContainKey("schedule");
        originalChat.Metadata.Should().NotContainKey("connector.http.authorization");
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithAuthAndDefaultProjection_ShouldOnlyInjectSenderTokenIntoLlmControl()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("sender-token-2");
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange);
        var original = new ServiceInvocationRequest
        {
            CommandId = "cmd-invoke",
            CorrelationId = "corr-invoke",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                Metadata =
                {
                    ["trace"] = "kept",
                },
                LlmControl = new LLMControlContextPayload
                {
                    ModelOverride = "opus",
                },
            }),
        };
        var auth = new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
            new ScheduledServiceInvocationNyxIdSubjectRef("lark", "tenant-1", "ou-user-1"),
            "proxy"));

        await port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            original,
            auth,
            new Dictionary<string, string>
            {
                ["connector.http.authorization"] = "Bearer header-token",
                ["schedule"] = "scheduled",
            }));

        credentialExchange.Sources.Should().ContainSingle()
            .Which.Subject.ExternalUserId.Should().Be("ou-user-1");
        var invoked = invocationPort.Requests.Should().ContainSingle().Which;
        invoked.Should().NotBeSameAs(original);
        var invokedChat = invoked.Payload.Unpack<ChatRequestEvent>();
        invokedChat.LlmControl.SenderNyxIdAccessToken.Should().Be("sender-token-2");
        invokedChat.LlmControl.ModelOverride.Should().Be("opus");
        invokedChat.ConnectorHttpAuthorization.Should().BeEmpty();
        invokedChat.Metadata.Should().Contain("trace", "kept");
        invokedChat.Metadata.Should().NotContainKey("connector.http.authorization");
        invokedChat.Metadata.Should().Contain("schedule", "scheduled");
        invokedChat.Metadata.Should().NotContainValue("sender-token-2");
        invokedChat.Metadata.Should().NotContainValue("Bearer sender-token-2");
        var originalChat = original.Payload.Unpack<ChatRequestEvent>();
        originalChat.LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
        originalChat.LlmControl.ModelOverride.Should().Be("opus");
        originalChat.ConnectorHttpAuthorization.Should().BeEmpty();
        originalChat.Metadata.Should().Contain("trace", "kept");
        originalChat.Metadata.Should().NotContainKey("schedule");
        originalChat.Metadata.Should().NotContainKey("connector.http.authorization");
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithWorkflowScheduledInvocationAgentKey_ShouldBorrowExactHandleWithoutVaultAccess()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("unused");
        var vault = new RecordingSecretVault("must-not-resolve");
        var expiresAt = DateTimeOffset.UtcNow.AddDays(7);
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange, vault);
        var reference = new SecretReference
        {
            Ref = "sec-agent-key",
            Purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
            OwnerScopeKey = "scope-key",
            ExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds(),
        };
        var auth = new ScheduledServiceInvocationAuth(
            ScheduledInvocationAgentKey: new ScheduledInvocationAgentKeyCredentialReference(
                reference,
                "key-schedule",
                expiresAt.ToUnixTimeMilliseconds()))
        {
            CallerAuthority = new ScheduledCallerNyxIdAuthority
            {
                Platform = "lark",
                Tenant = "tenant-alpha",
                ExternalUserId = "sender-alpha",
                Scope = "proxy",
                BindingId = "bnd-owner-alpha",
            },
        };

        await port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                CommandId = "cmd-invoke",
                CorrelationId = "corr-invoke",
                Payload = Any.Pack(CreateOwnerLLMChatRequest("hello")),
            },
            auth,
            ProjectNyxIdAccessTokenToWorkflowCallerCredential: true,
            AuthorizationFact: CreateAuthorizationFactDispatch(expiresAt.AddHours(-1)).AuthorizationFact));

        credentialExchange.Sources.Should().BeEmpty();
        vault.ResolveRequests.Should().BeEmpty();
        vault.StoreRequests.Should().BeEmpty();
        var invokedChat = invocationPort.Requests.Should().ContainSingle().Which.Payload.Unpack<ChatRequestEvent>();
        invokedChat.CallerDurableCredential.Ref.Should().Be(reference.Ref);
        invokedChat.CallerDurableCredential.Purpose.Should().Be(reference.Purpose);
        invokedChat.CallerDurableCredential.OwnerScopeKey.Should().Be(reference.OwnerScopeKey);
        invokedChat.CallerDurableCredential.SubjectId.Should().Be("key-schedule");
        invokedChat.CallerDurableCredential.SourceKind.Should().Be(DurableCallerCredentialSourceKind.ScheduledDispatch);
        invokedChat.CallerDurableCredential.ScheduledCallerNyxIdAuthority.Should().BeEquivalentTo(
            new ScheduledCallerNyxIdAuthority
            {
                Platform = "lark",
                Tenant = "tenant-alpha",
                ExternalUserId = "sender-alpha",
                Scope = "proxy",
                BindingId = "bnd-owner-alpha",
            });
        invokedChat.LlmControl.NyxIdAccessToken.Should().BeEmpty();
        invokedChat.LlmControl.NyxIdOrgToken.Should().BeEmpty();
        invokedChat.LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
        invokedChat.ConnectorHttpAuthorization.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithWrongPurposeWorkflowAgentKey_ShouldRejectBeforeDispatch()
    {
        var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("unused");
        var vault = new RecordingSecretVault("must-not-resolve");
        var port = new ScheduledServiceInvocationDispatchPort(
            invocationPort,
            credentialExchange,
            vault,
            timeProvider: new FixedTimeProvider(now));
        var dispatch = CreateAuthorizationFactDispatch(now);
        dispatch.Auth!.ScheduledInvocationAgentKey!.SecretReference.Purpose =
            CredentialSecretPurposes.ScheduledNyxApiKey;

        var act = () => port.DispatchAsync(dispatch);

        var failure = await act.Should().ThrowAsync<ScheduledServiceInvocationAuthorizationException>();
        failure.WithMessage("Scheduled invocation agent key secret reference purpose is invalid.");
        failure.Which.Code.Should()
            .Be(ScheduledServiceInvocationAuthorizationFailureCode.CredentialReferenceInvalid);
        invocationPort.Requests.Should().BeEmpty();
        credentialExchange.Sources.Should().BeEmpty();
        vault.ResolveRequests.Should().BeEmpty();
        vault.StoreRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("platform")]
    [InlineData("externalUserId")]
    [InlineData("scope")]
    [InlineData("bindingId")]
    public async Task ScheduledServiceInvocationDispatchPort_WithIncompleteWorkflowAgentKeyCallerAuthority_ShouldRejectBeforeCredentialAccess(
        string? missingField)
    {
        var now = new DateTimeOffset(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("unused");
        var vault = new RecordingSecretVault("must-not-resolve");
        var port = new ScheduledServiceInvocationDispatchPort(
            invocationPort,
            credentialExchange,
            vault,
            timeProvider: new FixedTimeProvider(now));
        var dispatch = CreateAuthorizationFactDispatch(now);
        dispatch = dispatch with
        {
            Auth = dispatch.Auth! with
            {
                CallerAuthority = CreateIncompleteCallerAuthority(missingField),
            },
        };

        var act = () => port.DispatchAsync(dispatch);

        var failure = await act.Should().ThrowAsync<ScheduledServiceInvocationAuthorizationException>();
        failure.Which.Code.Should().Be(ScheduledServiceInvocationAuthorizationFailureCode.CallerAuthorityInvalid);
        failure.Which.StableCode.Should().Be("caller_authority_invalid");
        invocationPort.Requests.Should().BeEmpty();
        credentialExchange.Sources.Should().BeEmpty();
        vault.ResolveRequests.Should().BeEmpty();
        vault.StoreRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithBorrowedAgentKey_WhenInvocationFails_ShouldNotRevokeHandle()
    {
        var invocationFailure = new InvalidOperationException("invocation failed");
        var invocationPort = new RecordingServiceInvocationPort(invocationFailure);
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("unused");
        var vault = new RecordingSecretVault("must-not-resolve");
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange, vault);
        var expiresAt = DateTimeOffset.UtcNow.AddDays(7);
        var reference = new SecretReference
        {
            Ref = "sec-borrowed-agent-key",
            Purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
            OwnerScopeKey = "scope-key",
        };

        var act = () => port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                CommandId = "cmd-failing-invoke",
                Payload = Any.Pack(CreateOwnerLLMChatRequest("hello")),
            },
            new ScheduledServiceInvocationAuth(new ScheduledInvocationAgentKeyCredentialReference(
                reference,
                "key-borrowed",
                expiresAt.ToUnixTimeMilliseconds()))
            {
                CallerAuthority = CreateCallerAuthority(),
            },
            ProjectNyxIdAccessTokenToWorkflowCallerCredential: true,
            AuthorizationFact: CreateAuthorizationFactDispatch(expiresAt.AddHours(-1)).AuthorizationFact));

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Should().BeSameAs(invocationFailure);
        var attemptedChat = invocationPort.Requests.Should().ContainSingle().Which.Payload.Unpack<ChatRequestEvent>();
        attemptedChat.CallerDurableCredential.Ref.Should().Be(reference.Ref);
        attemptedChat.CallerDurableCredential.Purpose.Should().Be(reference.Purpose);
        attemptedChat.CallerDurableCredential.OwnerScopeKey.Should().Be(reference.OwnerScopeKey);
        attemptedChat.CallerDurableCredential.SubjectId.Should().Be("key-borrowed");
        attemptedChat.CallerDurableCredential.SourceKind.Should()
            .Be(DurableCallerCredentialSourceKind.ScheduledDispatch);
        credentialExchange.Sources.Should().BeEmpty();
        vault.ResolveRequests.Should().BeEmpty();
        vault.StoreRequests.Should().BeEmpty();
        vault.RevokeRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ref", "Scheduled invocation agent key secret reference is missing.",
        ScheduledServiceInvocationAuthorizationFailureCode.CredentialReferenceMissing)]
    [InlineData("purpose", "Scheduled invocation agent key secret reference purpose is invalid.",
        ScheduledServiceInvocationAuthorizationFailureCode.CredentialReferenceInvalid)]
    [InlineData("ownerScopeKey", "Scheduled invocation agent key owner scope is missing.",
        ScheduledServiceInvocationAuthorizationFailureCode.CredentialReferenceInvalid)]
    [InlineData("apiKeyId", "Scheduled invocation agent key id is missing.",
        ScheduledServiceInvocationAuthorizationFailureCode.ApiKeyIdMissing)]
    public async Task ScheduledServiceInvocationDispatchPort_WithIncompleteWorkflowScheduledInvocationAgentKey_ShouldFailBeforeDispatch(
        string missingField,
        string expectedMessage,
        ScheduledServiceInvocationAuthorizationFailureCode expectedCode)
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("unused");
        var vault = new RecordingSecretVault("must-not-resolve");
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange, vault);
        var reference = new SecretReference
        {
            Ref = missingField == "ref" ? " " : "sec-agent-key",
            Purpose = missingField == "purpose" ? " " : CredentialSecretPurposes.ScheduledInvocationAgentKey,
            OwnerScopeKey = missingField == "ownerScopeKey" ? " " : "scope-key",
        };
        var apiKeyId = missingField == "apiKeyId" ? " " : "key-schedule";
        var expiresAt = DateTimeOffset.UtcNow.AddDays(7);

        var act = () => port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                Payload = Any.Pack(CreateOwnerLLMChatRequest("hello")),
            },
            new ScheduledServiceInvocationAuth(new ScheduledInvocationAgentKeyCredentialReference(
                reference,
                apiKeyId,
                expiresAt.ToUnixTimeMilliseconds()))
            {
                CallerAuthority = CreateCallerAuthority(),
            },
            ProjectNyxIdAccessTokenToWorkflowCallerCredential: true,
            AuthorizationFact: CreateAuthorizationFactDispatch(expiresAt.AddHours(-1)).AuthorizationFact));

        var failure = await act.Should().ThrowAsync<ScheduledServiceInvocationAuthorizationException>();
        failure.WithMessage(expectedMessage);
        failure.Which.Code.Should().Be(expectedCode);
        invocationPort.Requests.Should().BeEmpty();
        credentialExchange.Sources.Should().BeEmpty();
        vault.ResolveRequests.Should().BeEmpty();
        vault.StoreRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithNonWorkflowScheduledInvocationAgentKey_ShouldResolveAndInjectOwnerTokens()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("unused");
        var vault = new InMemorySecretVault();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(7);
        var stored = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "scope-key",
            "key-schedule",
            "agent-key-token",
            "test scheduled invocation",
            expiresAt));
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange, vault);

        await port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
            },
            new ScheduledServiceInvocationAuth(new ScheduledInvocationAgentKeyCredentialReference(
                stored.Reference,
                "key-schedule",
                expiresAt.ToUnixTimeMilliseconds())),
            AuthorizationFact: CreateAuthorizationFactDispatch(expiresAt.AddHours(-1)).AuthorizationFact));

        var invokedChat = invocationPort.Requests.Should().ContainSingle().Which.Payload.Unpack<ChatRequestEvent>();
        invokedChat.CallerDurableCredential.Should().BeNull();
        invokedChat.LlmControl.NyxIdAccessToken.Should().Be("agent-key-token");
        invokedChat.LlmControl.NyxIdOrgToken.Should().Be("agent-key-token");
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithExpiredScheduledInvocationAgentKey_ShouldFailBeforeResolve()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("unused");
        var vault = new InMemorySecretVault();
        var stored = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "scope-key",
            "key-schedule",
            "agent-key-token",
            "test scheduled invocation",
            DateTimeOffset.UtcNow.AddDays(7)));
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange, vault);
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var auth = new ScheduledServiceInvocationAuth(
            ScheduledInvocationAgentKey: new ScheduledInvocationAgentKeyCredentialReference(
                stored.Reference,
                "key-schedule",
                expiredAt.ToUnixTimeMilliseconds()));

        var act = () => port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                CommandId = "cmd-invoke",
                CorrelationId = "corr-invoke",
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
            },
            auth,
            AuthorizationFact: CreateAuthorizationFactDispatch(DateTimeOffset.UtcNow).AuthorizationFact));

        var failure = await act.Should().ThrowAsync<ScheduledServiceInvocationAuthorizationException>();
        failure.WithMessage("Scheduled invocation agent key is expired.");
        failure.Which.Code.Should()
            .Be(ScheduledServiceInvocationAuthorizationFailureCode.CredentialExpired);
        credentialExchange.Sources.Should().BeEmpty();
        invocationPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithScheduledInvocationAgentKeyAndNoVault_ShouldFailBeforeInvocation()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("unused");
        var expiresAt = DateTimeOffset.UtcNow.AddDays(7);
        var expiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds();
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange);
        var auth = new ScheduledServiceInvocationAuth(
            ScheduledInvocationAgentKey: new ScheduledInvocationAgentKeyCredentialReference(
                new SecretReference
                {
                    Ref = "sec-missing",
                    Purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
                    OwnerScopeKey = "scope-key",
                    ExpiresAtUnixMs = expiresAtUnixMs,
                },
                "key-schedule",
                expiresAtUnixMs));

        var act = () => port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                CommandId = "cmd-invoke",
                CorrelationId = "corr-invoke",
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
            },
            auth,
            AuthorizationFact: CreateAuthorizationFactDispatch(expiresAt.AddHours(-1)).AuthorizationFact));

        var failure = await act.Should().ThrowAsync<ScheduledServiceInvocationAuthorizationException>();
        failure.WithMessage("Scheduled invocation agent key resolver is not configured.");
        failure.Which.Code.Should()
            .Be(ScheduledServiceInvocationAuthorizationFailureCode.CredentialVaultUnavailable);
        credentialExchange.Sources.Should().BeEmpty();
        invocationPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithUnresolvedScheduledInvocationAgentKey_ShouldFailBeforeInvocation()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("unused");
        var vault = new InMemorySecretVault();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(7);
        var expiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds();
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange, vault);
        var auth = new ScheduledServiceInvocationAuth(
            ScheduledInvocationAgentKey: new ScheduledInvocationAgentKeyCredentialReference(
                new SecretReference
                {
                    Ref = "sec-missing",
                    Purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
                    OwnerScopeKey = "scope-key",
                    ExpiresAtUnixMs = expiresAtUnixMs,
                },
                "key-schedule",
                expiresAtUnixMs));

        var act = () => port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                CommandId = "cmd-invoke",
                CorrelationId = "corr-invoke",
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
            },
            auth,
            AuthorizationFact: CreateAuthorizationFactDispatch(expiresAt.AddHours(-1)).AuthorizationFact));

        var failure = await act.Should().ThrowAsync<ScheduledServiceInvocationAuthorizationException>();
        failure.WithMessage("Scheduled invocation agent key could not be resolved.");
        failure.Which.Code.Should()
            .Be(ScheduledServiceInvocationAuthorizationFailureCode.CredentialUnresolvable);
        failure.Which.StableCode.Should().Be("credential_unresolvable");
        credentialExchange.Sources.Should().BeEmpty();
        invocationPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithAuthAndNonChatPayload_ShouldExchangeWithoutInjectingToken()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("sender-token-1");
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange);
        var auth = new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
            new ScheduledServiceInvocationNyxIdSubjectRef("lark", "tenant-1", "ou-user-1"),
            "proxy"));

        await port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                CommandId = "cmd-invoke",
                CorrelationId = "corr-invoke",
                Payload = Any.Pack(new StringValue { Value = "invoke" }),
            },
            auth));

        credentialExchange.Sources.Should().ContainSingle();
        invocationPort.Requests.Should().ContainSingle()
            .Which.Payload.Unpack<StringValue>().Value.Should().Be("invoke");
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithAuthExchangeFailure_ShouldNotInvokeService()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort(error: "exchange failed");
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange);
        var auth = new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
            new ScheduledServiceInvocationNyxIdSubjectRef("lark", "tenant-1", "ou-user-1"),
            "proxy"));

        var act = () => port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                CommandId = "cmd-invoke",
                CorrelationId = "corr-invoke",
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
            },
            auth));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("exchange failed");
        credentialExchange.Sources.Should().ContainSingle();
        invocationPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithDurableCredentialReference_ShouldResolveVaultAndProjectDurableCallerCredential()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("ignored-subject-token");
        var secretVault = new RecordingSecretVault("durable-run-key");
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange, secretVault);
        var original = new ServiceInvocationRequest
        {
            CommandId = "cmd-invoke",
            CorrelationId = "corr-invoke",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                LlmControl = new LLMControlContextPayload { ModelOverride = "opus" },
            }),
        };
        var auth = new ScheduledServiceInvocationAuth(CreateDurableCredentialReference())
        {
            CallerAuthority = new ScheduledCallerNyxIdAuthority
            {
                Platform = "lark",
                Tenant = "tenant-alpha",
                ExternalUserId = "sender-alpha",
                Scope = "proxy",
                BindingId = "bnd-owner-alpha",
            },
        };

        await port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            original,
            auth,
            Headers: null,
            ProjectNyxIdAccessTokenToWorkflowCallerCredential: true,
            ScheduleId: "schedule-durable"));

        credentialExchange.Sources.Should().BeEmpty();
        var resolve = secretVault.ResolveRequests.Should().ContainSingle().Which;
        resolve.Ref.Should().Be("sec-1");
        resolve.Purpose.Should().Be(CredentialSecretPurposes.ScheduledNyxApiKey);
        resolve.OwnerScopeKey.Should().Be("owner-scope-1");
        resolve.SubjectId.Should().Be("credential-1");
        var store = secretVault.StoreRequests.Should().ContainSingle().Which;
        store.Purpose.Should().Be(CredentialSecretPurposes.WorkflowCallerDurableBearerToken);
        store.OwnerScopeKey.Should().Be("schedule:schedule-durable");
        store.SubjectId.Should().Be("credential-1");
        store.Secret.Should().Be("durable-run-key");
        var invoked = invocationPort.Requests.Should().ContainSingle().Which;
        var invokedChat = invoked.Payload.Unpack<ChatRequestEvent>();
        invokedChat.LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
        invokedChat.ConnectorHttpAuthorization.Should().BeEmpty();
        invokedChat.CallerDurableCredential.Should().NotBeNull();
        invokedChat.CallerDurableCredential.Purpose.Should().Be(CredentialSecretPurposes.WorkflowCallerDurableBearerToken);
        invokedChat.CallerDurableCredential.OwnerScopeKey.Should().Be("schedule:schedule-durable");
        invokedChat.CallerDurableCredential.SubjectId.Should().Be("credential-1");
        invokedChat.CallerDurableCredential.SourceKind.Should().Be(DurableCallerCredentialSourceKind.ScheduledDispatch);
        invokedChat.CallerDurableCredential.ScheduledCallerNyxIdAuthority.Should().BeEquivalentTo(
            new ScheduledCallerNyxIdAuthority
            {
                Platform = "lark",
                Tenant = "tenant-alpha",
                ExternalUserId = "sender-alpha",
                Scope = "proxy",
                BindingId = "bnd-owner-alpha",
            });
        invokedChat.LlmControl.ModelOverride.Should().Be("opus");
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithDurableCredentialReferenceAndDefaultProjection_ShouldInjectSenderToken()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("ignored-subject-token");
        var secretVault = new RecordingSecretVault("durable-run-key");
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange, secretVault);
        var original = new ServiceInvocationRequest
        {
            CommandId = "cmd-invoke",
            CorrelationId = "corr-invoke",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                LlmControl = new LLMControlContextPayload { ModelOverride = "opus" },
            }),
        };
        var auth = new ScheduledServiceInvocationAuth(CreateDurableCredentialReference());

        await port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            original,
            auth,
            ScheduleId: "schedule-durable"));

        credentialExchange.Sources.Should().BeEmpty();
        var resolve = secretVault.ResolveRequests.Should().ContainSingle().Which;
        resolve.Ref.Should().Be("sec-1");
        resolve.Purpose.Should().Be(CredentialSecretPurposes.ScheduledNyxApiKey);
        resolve.OwnerScopeKey.Should().Be("owner-scope-1");
        resolve.SubjectId.Should().Be("credential-1");
        secretVault.StoreRequests.Should().BeEmpty();
        var invoked = invocationPort.Requests.Should().ContainSingle().Which;
        var invokedChat = invoked.Payload.Unpack<ChatRequestEvent>();
        invokedChat.LlmControl.SenderNyxIdAccessToken.Should().Be("durable-run-key");
        invokedChat.LlmControl.NyxIdAccessToken.Should().BeEmpty();
        invokedChat.LlmControl.NyxIdOrgToken.Should().BeEmpty();
        invokedChat.LlmControl.ModelOverride.Should().Be("opus");
        invokedChat.CallerDurableCredential.Should().BeNull();
        original.Payload.Unpack<ChatRequestEvent>().LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(DurableCredentialReferenceFailureCases))]
    public async Task ScheduledServiceInvocationDispatchPort_WithDurableCredentialReferenceFailure_ShouldFailBeforeInvocation(
        string scenario,
        ISecretVault? secretVault,
        ScheduledServiceInvocationDurableCredentialReference credential,
        string expectedMessage,
        int expectedResolveCount)
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("ignored-subject-token");
        var port = secretVault == null
            ? new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange)
            : new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange, secretVault);
        var auth = new ScheduledServiceInvocationAuth(credential);

        var act = () => port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                CommandId = "cmd-invoke",
                CorrelationId = "corr-invoke",
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
            },
            auth,
            ProjectNyxIdAccessTokenToWorkflowCallerCredential: true,
            ScheduleId: "schedule-durable"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(expectedMessage, because: scenario);
        credentialExchange.Sources.Should().BeEmpty();
        invocationPort.Requests.Should().BeEmpty();
        if (secretVault is RecordingSecretVault recordingSecretVault)
        {
            recordingSecretVault.ResolveRequests.Should().HaveCount(expectedResolveCount);
            recordingSecretVault.StoreRequests.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Bearer sender-token")]
    [InlineData("sender token")]
    public async Task ScheduledServiceInvocationDispatchPort_WithAuthMalformedToken_ShouldFailBeforeInvocation(
        string accessToken)
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort(accessToken);
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange);
        var auth = new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
            new ScheduledServiceInvocationNyxIdSubjectRef("lark", "tenant-1", "ou-user-1"),
            "proxy"));

        var act = () => port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                CommandId = "cmd-invoke",
                CorrelationId = "corr-invoke",
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
            },
            auth));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(string.IsNullOrWhiteSpace(accessToken)
                ? "Scheduled service invocation sender NyxID credential exchange returned an empty access token."
                : "Scheduled service invocation sender NyxID credential exchange returned an invalid access token.");
        credentialExchange.Sources.Should().ContainSingle();
        invocationPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithHeaders_ShouldCloneAndAttachHeadersToChatMetadata()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var port = new ScheduledServiceInvocationDispatchPort(
            invocationPort,
            new RecordingScheduledServiceInvocationCredentialExchangePort());
        var original = new ServiceInvocationRequest
        {
            CommandId = "cmd-invoke",
            CorrelationId = "corr-invoke",
            Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
        };

        await port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            original,
            Headers: new Dictionary<string, string> { ["trace"] = "scheduled" }));

        var invoked = invocationPort.Requests.Should().ContainSingle().Which;
        invoked.Should().NotBeSameAs(original);
        invoked.Payload.Unpack<ChatRequestEvent>().Metadata.Should().Contain("trace", "scheduled");
        original.Payload.Unpack<ChatRequestEvent>().Metadata.Should().BeEmpty();
    }

    [Fact]
    public async Task NoopScheduledServiceInvocationCredentialExchangePort_ShouldReturnConfiguredFailure()
    {
        var port = new NoopScheduledServiceInvocationCredentialExchangePort();

        var result = await port.IssueNyxIdAsync(CreateCredentialSource());

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("Scheduled service invocation sender NyxID credential exchange is not configured.");
    }

    [Fact]
    public async Task NyxIdScheduledServiceInvocationCredentialExchangePort_ShouldIssueTokenForScopeOwnerAndScope()
    {
        var broker = new RecordingCapabilityBroker { AccessToken = " owner-token " };
        var port = new NyxIdScheduledServiceInvocationCredentialExchangePort(
            broker,
            NullLogger<NyxIdScheduledServiceInvocationCredentialExchangePort>.Instance);

        var result = await port.IssueNyxIdAsync(
            new ScheduledServiceInvocationNyxIdCredentialSource(
                new ScheduledServiceInvocationNyxIdSubjectRef(OwnerScope.NyxIdPlatform, string.Empty, "owner-nyx-user"),
                "proxy",
                ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner));

        result.Succeeded.Should().BeTrue();
        result.AccessToken.Should().Be(" owner-token ");
        broker.Subjects.Should().ContainSingle().Which.Should().BeEquivalentTo(new ExternalSubjectRef
        {
            Platform = OwnerScope.NyxIdPlatform,
            Tenant = string.Empty,
            ExternalUserId = "owner-nyx-user",
        });
        broker.Scopes.Should().ContainSingle().Which.Value.Should().Be("proxy");
    }

    [Fact]
    public async Task NyxIdScheduledServiceInvocationCredentialExchangePort_ShouldRejectScopeOwnerWithoutPersistedSubject()
    {
        var port = new NyxIdScheduledServiceInvocationCredentialExchangePort(
            new RecordingCapabilityBroker(),
            NullLogger<NyxIdScheduledServiceInvocationCredentialExchangePort>.Instance);

        var act = () => port.IssueNyxIdAsync(
            new ScheduledServiceInvocationNyxIdCredentialSource(
                null!,
                "proxy",
                ScheduledServiceInvocationNyxIdCredentialRole.ScopeOwner));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*scope owner NyxID subject is required*");
    }

    [Fact]
    public async Task NyxIdScheduledServiceInvocationCredentialExchangePort_ShouldIssueTokenForSubjectAndScope()
    {
        var expiresAtUnix = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var broker = new RecordingCapabilityBroker
        {
            AccessToken = " sender-token ",
            ExpiresAtUnix = expiresAtUnix,
        };
        var port = new NyxIdScheduledServiceInvocationCredentialExchangePort(
            broker,
            NullLogger<NyxIdScheduledServiceInvocationCredentialExchangePort>.Instance);

        var result = await port.IssueNyxIdAsync(CreateCredentialSource());

        result.Succeeded.Should().BeTrue();
        result.AccessToken.Should().Be(" sender-token ");
        result.ExpiresAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix));
        broker.Subjects.Should().ContainSingle().Which.Should().BeEquivalentTo(new ExternalSubjectRef
        {
            Platform = "lark",
            Tenant = "tenant-1",
            ExternalUserId = "ou-user-1",
        });
        broker.Scopes.Should().ContainSingle().Which.Value.Should().Be("proxy");
    }

    [Theory]
    [InlineData("not-found")]
    [InlineData("revoked")]
    [InlineData("scope")]
    [InlineData("service")]
    [InlineData("empty-token")]
    [InlineData("unexpected")]
    public async Task NyxIdScheduledServiceInvocationCredentialExchangePort_ShouldMapBrokerFailures(string failure)
    {
        var broker = new RecordingCapabilityBroker { Failure = failure };
        var port = new NyxIdScheduledServiceInvocationCredentialExchangePort(
            broker,
            NullLogger<NyxIdScheduledServiceInvocationCredentialExchangePort>.Instance);

        var result = await port.IssueNyxIdAsync(CreateCredentialSource());

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(failure switch
        {
            "not-found" => "NyxID binding was not found for the scheduled subject.",
            "revoked" => "NyxID binding was revoked for the scheduled subject.",
            "scope" => "NyxID binding does not grant the requested schedule scope.",
            "service" => "NyxID binding does not grant the required Aevatar service.",
            "empty-token" => "NyxID credential exchange returned an empty access token.",
            _ => "NyxID credential exchange failed.",
        });
    }

    [Fact]
    public async Task NyxIdScheduledServiceInvocationCredentialExchangePort_ShouldPropagateCancellation()
    {
        var broker = new RecordingCapabilityBroker();
        var port = new NyxIdScheduledServiceInvocationCredentialExchangePort(
            broker,
            NullLogger<NyxIdScheduledServiceInvocationCredentialExchangePort>.Instance);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => port.IssueNyxIdAsync(CreateCredentialSource(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        broker.Subjects.Should().BeEmpty();
    }

    [Fact]
    public void ScheduledServiceInvocationDispatchPort_ShouldNotImplementActorDispatchPort()
    {
        typeof(ScheduledServiceInvocationDispatchPort)
            .Should()
            .NotBeAssignableTo<IActorDispatchPort>();
    }

    public static TheoryData<string, ISecretVault?, ScheduledServiceInvocationDurableCredentialReference, string, int>
        DurableCredentialReferenceFailureCases() => new()
        {
            {
                "missing-vault",
                null,
                CreateDurableCredentialReference(),
                "Scheduled service invocation durable credential vault is not configured.",
                0
            },
            {
                "incomplete-reference",
                new RecordingSecretVault("durable-run-key"),
                CreateDurableCredentialReference(referenceRef: " "),
                "Scheduled service invocation durable credential reference is incomplete.",
                0
            },
            {
                "wrong-purpose",
                new RecordingSecretVault("durable-run-key"),
                CreateDurableCredentialReference(purpose: "wrong-purpose"),
                "Scheduled service invocation durable credential reference purpose is invalid.",
                0
            },
            {
                "unresolved-secret",
                new RecordingSecretVault(null),
                CreateDurableCredentialReference(),
                "Scheduled service invocation durable credential reference could not be resolved.",
                1
            },
            {
                "empty-secret",
                new RecordingSecretVault(string.Empty, resolveWhitespaceSecret: true),
                CreateDurableCredentialReference(),
                "Scheduled service invocation durable credential reference resolved an empty access token.",
                1
            },
            {
                "malformed-secret",
                new RecordingSecretVault("Bearer durable-run-key"),
                CreateDurableCredentialReference(),
                "Scheduled service invocation durable credential reference resolved an invalid access token.",
                1
            },
        };

    public static TheoryData<
        string,
        Func<ScheduledServiceInvocationDispatchRequest, ScheduledServiceInvocationDispatchRequest>>
        InvalidAuthorizationFactCases() => new()
        {
            {
                "missing-required-field",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with { PermissionDigest = " " },
                }
            },
            {
                "missing-policy-version",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with { PolicyVersion = " " },
                }
            },
            {
                "missing-owner-authority",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with
                    {
                        Owner = dispatch.AuthorizationFact.Owner with { Authority = " " },
                    },
                }
            },
            {
                "missing-owner-subject",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with
                    {
                        Owner = dispatch.AuthorizationFact.Owner with { OwnerSubject = " " },
                    },
                }
            },
            {
                "missing-scopes",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with { Scopes = " " },
                }
            },
            {
                "expired-fact",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with
                    {
                        ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(1),
                    },
                }
            },
            {
                "invalid-service-node-grant",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with
                    {
                        ServiceGrants =
                        [
                            new ScheduledInvocationAuthorizationServiceGrant("svc-without-node", [], false),
                        ],
                    },
                }
            },
            {
                "blank-service-node-id",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with
                    {
                        ServiceGrants =
                        [
                            new ScheduledInvocationAuthorizationServiceGrant("svc-alpha", [" "], false),
                        ],
                    },
                }
            },
            {
                "unexpected-node-id-when-node-grants-not-required",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with
                    {
                        ServiceGrants =
                        [
                            new ScheduledInvocationAuthorizationServiceGrant("svc-alpha", ["node-alpha"], true),
                        ],
                    },
                }
            },
            {
                "missing-service-id",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with
                    {
                        ServiceGrants =
                        [
                            new ScheduledInvocationAuthorizationServiceGrant(" ", ["node-alpha"], false),
                        ],
                    },
                }
            },
            {
                "missing-required-service-grants",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with { ServiceGrants = [] },
                }
            },
            {
                "invalid-catalog-version",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with
                    {
                        Authority = dispatch.AuthorizationFact.Authority with { CatalogStateVersion = 0 },
                    },
                }
            },
            {
                "missing-catalog-content-digest",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with
                    {
                        Authority = dispatch.AuthorizationFact.Authority with { CatalogContentDigest = " " },
                    },
                }
            },
            {
                "missing-catalog-contract-version",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with
                    {
                        Authority = dispatch.AuthorizationFact.Authority with { CatalogContractVersion = " " },
                    },
                }
            },
            {
                "missing-catalog-policy-version",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with
                    {
                        Authority = dispatch.AuthorizationFact.Authority with { CatalogPolicyVersion = " " },
                    },
                }
            },
            {
                "missing-catalog-evaluated-at",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with
                    {
                        Authority = dispatch.AuthorizationFact.Authority with { CatalogEvaluatedAt = default },
                    },
                }
            },
            {
                "not-dedicated-to-schedule",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with
                    {
                        Disclosure = dispatch.AuthorizationFact.Disclosure with { DedicatedToSchedule = false },
                    },
                }
            },
            {
                "secret-not-managed-by-aevatar",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with
                    {
                        Disclosure = dispatch.AuthorizationFact.Disclosure with { SecretManagedByAevatar = false },
                    },
                }
            },
            {
                "unsafe-disclosure",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with
                    {
                        Disclosure = dispatch.AuthorizationFact.Disclosure with { BrowserReceivesRawKey = true },
                    },
                }
            },
            {
                "missing-scheduled-agent-key",
                dispatch => dispatch with { Auth = null }
            },
            {
                "key-outlives-fact",
                dispatch => dispatch with
                {
                    Auth = CreateScheduledAgentKeyAuth(dispatch.AuthorizationFact!.ExpiresAt.AddMinutes(1)),
                }
            },
        };

    public static TheoryData<
        string,
        Func<ScheduledServiceInvocationDispatchRequest, ScheduledServiceInvocationDispatchRequest>>
        InvalidOwnerLLMSelectionCases() => new()
        {
            {
                "missing-selection",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with { OwnerLLMSelection = null },
                }
            },
            {
                "malformed-selection",
                dispatch => ReplaceOwnerLLMSelection(dispatch, new ScheduledInvocationOwnerLLMSelection
                {
                    RouteKind = LLMRouteKind.NyxIdUserService,
                    RouteValue = $" {OwnerLLMRoute}",
                    NyxIdUserServiceId = OwnerLLMServiceId,
                    ServiceSlugSnapshot = "chrono-llm-public",
                    Model = OwnerLLMModel,
                })
            },
            {
                "exact-service-not-granted",
                dispatch => dispatch with
                {
                    AuthorizationFact = dispatch.AuthorizationFact! with
                    {
                        ServiceGrants = dispatch.AuthorizationFact.ServiceGrants
                            .Where(grant => !string.Equals(
                                grant.ServiceId,
                                OwnerLLMServiceId,
                                StringComparison.Ordinal))
                            .ToArray(),
                    },
                }
            },
        };

    public static TheoryData<
        string,
        Func<ScheduledServiceInvocationDispatchRequest, ScheduledServiceInvocationDispatchRequest>>
        OwnerLLMPayloadMismatchCases() => new()
        {
            {
                "non-chat-payload",
                dispatch => ReplacePayload(dispatch, Any.Pack(new StringValue { Value = "not-chat" }))
            },
            {
                "non-chat-payload-without-owner-llm-source-stamp",
                dispatch => ReplacePayload(
                    RemoveOwnerLLMSourceStamp(dispatch),
                    Any.Pack(new StringValue { Value = "not-chat" }))
            },
            {
                "malformed-chat-payload",
                dispatch =>
                {
                    var payload = Any.Pack(new ChatRequestEvent());
                    payload.Value = Google.Protobuf.ByteString.CopyFrom(0x0A, 0x05, 0x01);
                    return ReplacePayload(dispatch, payload);
                }
            },
            {
                "route-mismatch",
                dispatch => ReplaceOwnerLLMPayload(dispatch, "/api/v1/proxy/s/other-llm", OwnerLLMModel)
            },
            {
                "gateway-route-mismatch",
                dispatch => ReplaceOwnerLLMPayload(
                    dispatch,
                    ScheduledInvocationOwnerLLMSelectionPolicy.GatewayRoute,
                    OwnerLLMModel)
            },
            {
                "model-mismatch",
                dispatch => ReplaceOwnerLLMPayload(dispatch, OwnerLLMRoute, "gpt-other")
            },
            {
                "case-different-model-mismatch",
                dispatch => ReplaceOwnerLLMPayload(dispatch, OwnerLLMRoute, OwnerLLMModel.ToUpperInvariant())
            },
            {
                "route-present-without-owner-llm-source-stamp",
                dispatch => ReplaceOwnerLLMPayload(
                    RemoveOwnerLLMSourceStamp(dispatch),
                    OwnerLLMRoute,
                    string.Empty)
            },
            {
                "model-present-without-owner-llm-source-stamp",
                dispatch => ReplaceOwnerLLMPayload(
                    RemoveOwnerLLMSourceStamp(dispatch),
                    string.Empty,
                    OwnerLLMModel)
            },
        };

    private static ScheduledServiceInvocationDispatchRequest ReplaceOwnerLLMSelection(
        ScheduledServiceInvocationDispatchRequest dispatch,
        ScheduledInvocationOwnerLLMSelection selection) =>
        dispatch with
        {
            AuthorizationFact = dispatch.AuthorizationFact! with { OwnerLLMSelection = selection },
        };

    private static ScheduledServiceInvocationDispatchRequest ReplacePayload(
        ScheduledServiceInvocationDispatchRequest dispatch,
        Any payload)
    {
        var request = dispatch.Request.Clone();
        request.Payload = payload;
        return dispatch with { Request = request };
    }

    private static ScheduledServiceInvocationDispatchRequest ReplaceOwnerLLMPayload(
        ScheduledServiceInvocationDispatchRequest dispatch,
        string route,
        string model)
    {
        var request = dispatch.Request.Clone();
        var chat = request.Payload.Unpack<ChatRequestEvent>();
        chat.LlmControl ??= new LLMControlContextPayload();
        chat.LlmControl.NyxIdRoutePreference = route;
        chat.LlmControl.ModelOverride = model;
        request.Payload = Any.Pack(chat);
        return dispatch with { Request = request };
    }

    private static ScheduledServiceInvocationDispatchRequest RemoveOwnerLLMSourceStamp(
        ScheduledServiceInvocationDispatchRequest dispatch) =>
        dispatch with
        {
            AuthorizationFact = dispatch.AuthorizationFact! with
            {
                Authority = dispatch.AuthorizationFact.Authority with { OwnerLlmStateVersion = 0 },
                OwnerLLMSelection = null,
            },
        };

    private static ScheduledCallerNyxIdAuthority CreateCallerAuthority() => new()
    {
        Platform = "lark",
        ExternalUserId = "sender-alpha",
        Scope = "proxy",
        BindingId = "bnd-owner-alpha",
    };

    private static ScheduledCallerNyxIdAuthority? CreateIncompleteCallerAuthority(string? missingField)
    {
        if (missingField == null)
            return null;

        var authority = CreateCallerAuthority();
        switch (missingField)
        {
            case "platform":
                authority.Platform = " ";
                break;
            case "externalUserId":
                authority.ExternalUserId = " ";
                break;
            case "scope":
                authority.Scope = " ";
                break;
            case "bindingId":
                authority.BindingId = " ";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(missingField), missingField, null);
        }

        return authority;
    }

    private static ScheduledInvocationOwnerLLMSelection CreateOwnerLLMSelection() => new()
    {
        RouteKind = LLMRouteKind.NyxIdUserService,
        RouteValue = OwnerLLMRoute,
        NyxIdUserServiceId = OwnerLLMServiceId,
        ServiceSlugSnapshot = "chrono-llm-public",
        Model = OwnerLLMModel,
    };

    private static ChatRequestEvent CreateOwnerLLMChatRequest(string prompt) => new()
    {
        Prompt = prompt,
        LlmControl = new LLMControlContextPayload
        {
            NyxIdRoutePreference = OwnerLLMRoute,
            ModelOverride = OwnerLLMModel,
        },
    };

    private static ScheduledServiceInvocationDispatchRequest CreateAuthorizationFactDispatch(DateTimeOffset now)
    {
        var factExpiresAt = now.AddHours(1);
        return new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                CommandId = "cmd-authorized",
                CorrelationId = "corr-authorized",
                Identity = new ServiceIdentity { ServiceId = "svc-alpha" },
                EndpointId = "chat",
                Payload = Any.Pack(CreateOwnerLLMChatRequest("run")),
            },
            CreateScheduledAgentKeyAuth(factExpiresAt),
            ProjectNyxIdAccessTokenToWorkflowCallerCredential: true,
            ScheduleId: "schedule-authorized",
            AuthorizationFact: new ScheduledInvocationAuthorizationFact(
                "digest-alpha",
                "policy-v1",
                new ScheduledInvocationAuthorizationOwner("nyxid", "personal", "owner-alpha"),
                [
                    new ScheduledInvocationAuthorizationServiceGrant("svc-alpha", ["node-alpha"], false),
                    new ScheduledInvocationAuthorizationServiceGrant(
                        OwnerLLMServiceId,
                        ["node-owner-llm"],
                        false),
                ],
                "proxy",
                factExpiresAt,
                ServiceGrantsNotRequired: false,
                new ScheduledInvocationAuthorizationDisclosure(true, true, false, true, true),
                new ScheduledInvocationAuthorizationAuthority(
                    11,
                    12,
                    13,
                    14,
                    15,
                    now.AddMinutes(-1),
                    now.AddMinutes(30),
                    "catalog-digest-alpha",
                    "scope-plan-contract/v1",
                    "scope-plan-policy/v1",
                    now.AddMinutes(-2)),
                CreateOwnerLLMSelection()));
    }

    private static ScheduledServiceInvocationAuth CreateScheduledAgentKeyAuth(DateTimeOffset expiresAt) =>
        new(new ScheduledInvocationAgentKeyCredentialReference(
            new SecretReference
            {
                Ref = "secret-alpha",
                Purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
                OwnerScopeKey = "owner-alpha",
                ExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds(),
            },
            "key-alpha",
            expiresAt.ToUnixTimeMilliseconds()))
        {
            CallerAuthority = CreateCallerAuthority(),
        };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ScheduledServiceInvocationNyxIdCredentialSource CreateCredentialSource() =>
        new(
            new ScheduledServiceInvocationNyxIdSubjectRef("lark", "tenant-1", "ou-user-1"),
            "proxy");

    private static ScheduledServiceInvocationDurableCredentialReference CreateDurableCredentialReference(
        string credentialId = "credential-1",
        string referenceRef = "sec-1",
        string purpose = CredentialSecretPurposes.ScheduledNyxApiKey,
        string ownerScopeKey = "owner-scope-1") =>
        new(
            credentialId,
            new SecretReference
            {
                Ref = referenceRef,
                Purpose = purpose,
                OwnerScopeKey = ownerScopeKey,
            });

    private static DurableCallerCredentialRef CreateDurableCallerCredentialRef() =>
        new()
        {
            Ref = "sec_scheduled",
            Purpose = CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
            OwnerScopeKey = "schedule:schedule-1",
            SubjectId = "lark:tenant-1:ou-user-1",
            SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
        };

    private sealed class RecordingServiceInvocationPort(Exception? failure = null) : IServiceInvocationPort
    {
        public List<ServiceInvocationRequest> Requests { get; } = [];

        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(
            ServiceInvocationRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.Clone());
            if (failure != null)
                throw failure;

            return Task.FromResult(new ServiceInvocationAcceptedReceipt
            {
                CommandId = request.CommandId,
                CorrelationId = request.CorrelationId,
                TargetActorId = "service-actor",
            });
        }
    }

    private sealed class RecordingScheduledServiceInvocationCredentialExchangePort(
        string? accessToken = null,
        string? error = null,
        DateTimeOffset? expiresAt = null) : IScheduledServiceInvocationCredentialExchangePort
    {
        public List<ScheduledServiceInvocationNyxIdCredentialSource> Sources { get; } = [];
        public Task<ScheduledServiceInvocationCredentialExchangeResult> IssueNyxIdAsync(
            ScheduledServiceInvocationNyxIdCredentialSource source,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Sources.Add(source);
            return Task.FromResult(CreateResult());
        }

        private ScheduledServiceInvocationCredentialExchangeResult CreateResult() =>
            error == null
                ? ScheduledServiceInvocationCredentialExchangeResult.Success(
                    accessToken ?? "sender-token",
                    expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5))
                : ScheduledServiceInvocationCredentialExchangeResult.Failure(error);
    }

    private sealed class RecordingSecretVault(
        string? secret,
        bool resolveWhitespaceSecret = false) : ISecretVault
    {
        public List<ResolveSecretRequest> ResolveRequests { get; } = [];
        public List<StoreSecretRequest> StoreRequests { get; } = [];
        public List<RevokeSecretRequest> RevokeRequests { get; } = [];
        private SecretReference? _storedReference;
        private string? _storedSubjectId;
        private string? _storedSecret;

        public Task<StoreSecretResult> PutAsync(StoreSecretRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StoreRequests.Add(request);
            _storedReference = new SecretReference
            {
                Ref = "sec-workflow-caller-1",
                Purpose = request.Purpose,
                OwnerScopeKey = request.OwnerScopeKey,
                Version = 1,
                ExpiresAtUnixMs = request.ExpiresAt?.ToUnixTimeMilliseconds() ?? 0,
            };
            _storedSubjectId = request.SubjectId;
            _storedSecret = request.Secret;
            return Task.FromResult(new StoreSecretResult(_storedReference.Clone()));
        }

        public Task<ResolveSecretResult> ResolveAsync(ResolveSecretRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ResolveRequests.Add(request);
            if (_storedReference != null &&
                string.Equals(request.Ref, _storedReference.Ref, StringComparison.Ordinal) &&
                string.Equals(request.Purpose, _storedReference.Purpose, StringComparison.Ordinal) &&
                string.Equals(request.OwnerScopeKey, _storedReference.OwnerScopeKey, StringComparison.Ordinal) &&
                string.Equals(request.SubjectId, _storedSubjectId, StringComparison.Ordinal))
            {
                return Task.FromResult(new ResolveSecretResult(_storedReference.Clone(), _storedSecret));
            }

            return Task.FromResult(secret == null || (string.IsNullOrWhiteSpace(secret) && !resolveWhitespaceSecret)
                ? new ResolveSecretResult(null, null)
                : new ResolveSecretResult(CreateDurableCredentialReference().SecretReference.Clone(), secret));
        }

        public Task<RotateSecretResult> RotateAsync(RotateSecretRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RevokeSecretResult> RevokeAsync(RevokeSecretRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RevokeRequests.Add(request);
            return Task.FromResult(new RevokeSecretResult(true));
        }
    }

    private sealed class RecordingCapabilityBroker : INyxIdCapabilityBroker
    {
        public string AccessToken { get; init; } = "sender-token";
        public long ExpiresAtUnix { get; init; } = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        public string? Failure { get; init; }
        public List<ExternalSubjectRef> Subjects { get; } = [];
        public List<CapabilityScope> Scopes { get; } = [];

        public Task<BindingChallenge> StartExternalBindingAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RevokeBindingAsync(
            ExternalSubjectRef externalSubject,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CapabilityHandle> IssueShortLivedAsync(
            ExternalSubjectRef externalSubject,
            CapabilityScope scope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Subjects.Add(externalSubject.Clone());
            Scopes.Add(scope.Clone());

            return Failure switch
            {
                "not-found" => throw new BindingNotFoundException(externalSubject),
                "revoked" => throw new BindingRevokedException(externalSubject),
                "scope" => throw new BindingScopeMismatchException(externalSubject),
                "service" => throw new BindingServiceAccessMismatchException(
                    externalSubject,
                    ["https://nyxid.test/api/v1/proxy/s/aevatar"]),
                "unexpected" => throw new InvalidOperationException("broker failed"),
                "empty-token" => Task.FromResult(new CapabilityHandle()),
                _ => Task.FromResult(new CapabilityHandle
                {
                    AccessToken = AccessToken,
                    ExpiresAtUnix = ExpiresAtUnix,
                }),
            };
        }

        public Task<CapabilityHandle> IssueShortLivedByBindingIdAsync(
            ExternalSubjectRef externalSubject,
            string bindingId,
            CapabilityScope scope,
            CancellationToken ct = default) =>
            IssueShortLivedAsync(externalSubject, scope, ct);
    }
}
