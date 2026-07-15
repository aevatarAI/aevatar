using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Application.Schedules;
using Aevatar.GAgentService.Hosting.Endpoints.Schedules;
using Aevatar.GAgentService.Infrastructure.Schedules;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

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
        request.ScheduleId.Should().Be("schedule-1");
        request.Payload.Unpack<StringValue>().Value.Should().Be("run");
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
                                CredentialRef = "tool-owner-secret",
                                OrganizationCredentialRef = "tool-org-secret",
                                SenderCredentialRef = "tool-sender-secret",
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
                            CredentialRef = "owner-secret",
                            OrganizationCredentialRef = "org-secret",
                            SenderCredentialRef = "sender-secret",
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
        persistedChat.Headers.Should().NotContainKey(ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey);
        persistedChat.Headers.Should().Contain("client", "kept");
        persistedChat.Metadata.Should().NotContainKey(ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey);
        persistedChat.Metadata.Should().Contain("trace", "kept");
        persistedChat.LlmControl.Should().NotBeNull();
        persistedChat.LlmControl.CredentialRef.Should().BeEmpty();
        persistedChat.LlmControl.OrganizationCredentialRef.Should().BeEmpty();
        persistedChat.LlmControl.SenderCredentialRef.Should().BeEmpty();
        persistedChat.ConnectorHttpAuthorization.Should().BeEmpty();
        persistedChat.CallerDurableCredential.Should().BeNull();
        persistedChat.LlmControl.ModelOverride.Should().Be("sonnet");
        persistedChat.LlmControl.NyxIdRoutePreference.Should().Be("low-latency");
        persistedChat.LlmControl.MaxToolRoundsOverride.Should().Be(3);
        persistedChat.LlmControl.UserMemoryPrompt.Should().Be("remember preferences");
        persistedChat.ToolContext.Credentials.CredentialRef.Should().BeEmpty();
        persistedChat.ToolContext.Credentials.OrganizationCredentialRef.Should().BeEmpty();
        persistedChat.ToolContext.Credentials.SenderCredentialRef.Should().BeEmpty();
        persistedChat.ToolContext.Routing.ModelOverride.Should().Be("tool-model");
        persistedChat.ToolContext.Routing.NyxIdRoutePreference.Should().Be("tool-route");
        persistedChat.ToolContext.Routing.MaxToolRoundsOverride.Should().Be(5);
        persistedChat.ToolContext.Routing.UserMemoryPrompt.Should().Be("tool memory");
        var descriptorChat = prepared.Descriptor.ServiceInvocation!.Payload.Unpack<ChatRequestEvent>();
        descriptorChat.ConnectorHttpAuthorization.Should().BeEmpty();
        descriptorChat.Headers.Should().NotContainKey(ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey);
        descriptorChat.Metadata.Should().NotContainKey(ScheduledServiceInvocationPayloadPolicy.ConnectorHttpAuthorizationKey);
        descriptorChat.LlmControl.SenderCredentialRef.Should().BeEmpty();
        descriptorChat.ConnectorHttpAuthorization.Should().BeEmpty();
        descriptorChat.CallerDurableCredential.Should().BeNull();
        descriptorChat.LlmControl.ModelOverride.Should().Be("sonnet");
        descriptorChat.ToolContext.Credentials.SenderCredentialRef.Should().BeEmpty();
        descriptorChat.ToolContext.Routing.ModelOverride.Should().Be("tool-model");
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
                                CredentialRef = "owner-secret",
                                OrganizationCredentialRef = "org-secret",
                                SenderCredentialRef = "sender-secret",
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
        persistedChat.ToolContext.Credentials.CredentialRef.Should().BeEmpty();
        persistedChat.ToolContext.Credentials.OrganizationCredentialRef.Should().BeEmpty();
        persistedChat.ToolContext.Credentials.SenderCredentialRef.Should().BeEmpty();
        persistedChat.ToolContext.Routing.ModelOverride.Should().Be("opus");
    }

    [Fact]
    public void ScheduledDispatchHttpRequest_ShouldRejectExternalCallerDurableCredential()
    {
        var envelopeTarget = new ScheduledDispatchEnvelopeTargetHttpRequest
        {
            ActorId = "target",
            Envelope = new EventEnvelope
            {
                Payload = Any.Pack(new ChatRequestEvent
                {
                    Prompt = "hello",
                    CallerDurableCredential = CreateDurableCallerCredentialRef(),
                }),
            },
        };
        var serviceTarget = new ScheduledDispatchServiceInvocationTargetHttpRequest
        {
            Identity = new ServiceIdentity { TenantId = "tenant", ServiceId = "svc" },
            EndpointId = "chat",
            PayloadTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
        };

        FluentActions.Invoking(() => envelopeTarget.ToTarget())
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*caller_durable_credential*trusted-only*");
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
            ScopeOwnerIdentity: new ScheduledServiceInvocationScopeOwnerCredentialSource("proxy"));

        await port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(original, auth));

        var exchangedSource = credentialExchange.Sources.Should().ContainSingle().Which;
        exchangedSource.Role.Should().Be(ScheduledServiceInvocationIdentityCredentialRole.ScopeOwner);
        exchangedSource.Scope.Should().Be("proxy");
        var invokedChat = invocationPort.Requests.Should().ContainSingle().Which.Payload.Unpack<ChatRequestEvent>();
        invokedChat.LlmControl.CredentialRef.Should().Be("owner-token");
        invokedChat.LlmControl.OrganizationCredentialRef.Should().Be("owner-token");
        invokedChat.LlmControl.SenderCredentialRef.Should().BeEmpty();
        invokedChat.ConnectorHttpAuthorization.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithWorkflowProjection_ShouldCarryAuthorityWithoutVaultOrExchange()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("owner-token");
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange);
        var auth = new ScheduledServiceInvocationAuth(
            ScopeOwnerIdentity: new ScheduledServiceInvocationScopeOwnerCredentialSource(
                "proxy",
                new ScheduledServiceInvocationIdentitySubject("nyxid", "tenant-1", "owner-1")));
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
        var auth = new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationIdentityCredentialSource(
            new ScheduledServiceInvocationIdentitySubject("lark", "tenant-1", "ou-user-1"),
            "proxy"));

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
        invokedChat.LlmControl.SenderCredentialRef.Should().BeEmpty();
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
            });
        invokedChat.Metadata.Should().Contain("trace", "kept");
        invokedChat.Metadata.Should().NotContainKey("connector.http.authorization");
        invokedChat.Metadata.Should().Contain("schedule", "scheduled");
        invokedChat.Metadata.Should().NotContainValue("sender-token-1");
        invokedChat.Metadata.Should().NotContainValue("Bearer sender-token-1");
        vault.StoreRequests.Should().BeEmpty();
        var originalChat = original.Payload.Unpack<ChatRequestEvent>();
        originalChat.LlmControl.SenderCredentialRef.Should().BeEmpty();
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
        var auth = new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationIdentityCredentialSource(
            new ScheduledServiceInvocationIdentitySubject("lark", "tenant-1", "ou-user-1"),
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
        invokedChat.LlmControl.SenderCredentialRef.Should().Be("sender-token-2");
        invokedChat.LlmControl.ModelOverride.Should().Be("opus");
        invokedChat.ConnectorHttpAuthorization.Should().BeEmpty();
        invokedChat.Metadata.Should().Contain("trace", "kept");
        invokedChat.Metadata.Should().NotContainKey("connector.http.authorization");
        invokedChat.Metadata.Should().Contain("schedule", "scheduled");
        invokedChat.Metadata.Should().NotContainValue("sender-token-2");
        invokedChat.Metadata.Should().NotContainValue("Bearer sender-token-2");
        var originalChat = original.Payload.Unpack<ChatRequestEvent>();
        originalChat.LlmControl.SenderCredentialRef.Should().BeEmpty();
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
                expiresAt.ToUnixTimeMilliseconds()));

        await port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                CommandId = "cmd-invoke",
                CorrelationId = "corr-invoke",
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
            },
            auth,
            ProjectNyxIdAccessTokenToWorkflowCallerCredential: true));

        credentialExchange.Sources.Should().BeEmpty();
        vault.ResolveRequests.Should().BeEmpty();
        vault.StoreRequests.Should().BeEmpty();
        var invokedChat = invocationPort.Requests.Should().ContainSingle().Which.Payload.Unpack<ChatRequestEvent>();
        invokedChat.CallerDurableCredential.Ref.Should().Be(reference.Ref);
        invokedChat.CallerDurableCredential.Purpose.Should().Be(reference.Purpose);
        invokedChat.CallerDurableCredential.OwnerScopeKey.Should().Be(reference.OwnerScopeKey);
        invokedChat.CallerDurableCredential.SubjectId.Should().Be("key-schedule");
        invokedChat.CallerDurableCredential.SourceKind.Should().Be(DurableCallerCredentialSourceKind.ScheduledDispatch);
        invokedChat.LlmControl.CredentialRef.Should().BeEmpty();
        invokedChat.LlmControl.OrganizationCredentialRef.Should().BeEmpty();
        invokedChat.LlmControl.SenderCredentialRef.Should().BeEmpty();
        invokedChat.ConnectorHttpAuthorization.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithBorrowedAgentKey_WhenInvocationFails_ShouldNotRevokeHandle()
    {
        var invocationFailure = new InvalidOperationException("invocation failed");
        var invocationPort = new RecordingServiceInvocationPort(invocationFailure);
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("unused");
        var vault = new RecordingSecretVault("must-not-resolve");
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange, vault);
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
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
            },
            new ScheduledServiceInvocationAuth(new ScheduledInvocationAgentKeyCredentialReference(
                reference,
                "key-borrowed",
                DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeMilliseconds())),
            ProjectNyxIdAccessTokenToWorkflowCallerCredential: true));

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
    [InlineData("ref", "Scheduled invocation agent key secret reference is missing.")]
    [InlineData("purpose", "Scheduled invocation agent key secret reference purpose is missing.")]
    [InlineData("ownerScopeKey", "Scheduled invocation agent key owner scope is missing.")]
    [InlineData("apiKeyId", "Scheduled invocation agent key id is missing.")]
    public async Task ScheduledServiceInvocationDispatchPort_WithIncompleteWorkflowScheduledInvocationAgentKey_ShouldFailBeforeDispatch(
        string missingField,
        string expectedMessage)
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

        var act = () => port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
            },
            new ScheduledServiceInvocationAuth(new ScheduledInvocationAgentKeyCredentialReference(
                reference,
                apiKeyId,
                DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeMilliseconds())),
            ProjectNyxIdAccessTokenToWorkflowCallerCredential: true));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(expectedMessage);
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
                expiresAt.ToUnixTimeMilliseconds()))));

        var invokedChat = invocationPort.Requests.Should().ContainSingle().Which.Payload.Unpack<ChatRequestEvent>();
        invokedChat.CallerDurableCredential.Should().BeNull();
        invokedChat.LlmControl.CredentialRef.Should().Be("agent-key-token");
        invokedChat.LlmControl.OrganizationCredentialRef.Should().Be("agent-key-token");
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
        var auth = new ScheduledServiceInvocationAuth(
            ScheduledInvocationAgentKey: new ScheduledInvocationAgentKeyCredentialReference(
                stored.Reference,
                "key-schedule",
                DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()));

        var act = () => port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            new ServiceInvocationRequest
            {
                CommandId = "cmd-invoke",
                CorrelationId = "corr-invoke",
                Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
            },
            auth));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Scheduled invocation agent key is expired.");
        credentialExchange.Sources.Should().BeEmpty();
        invocationPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithScheduledInvocationAgentKeyAndNoVault_ShouldFailBeforeInvocation()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("unused");
        var expiresAtUnixMs = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeMilliseconds();
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
            auth));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Scheduled invocation agent key resolver is not configured.");
        credentialExchange.Sources.Should().BeEmpty();
        invocationPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithUnresolvedScheduledInvocationAgentKey_ShouldFailBeforeInvocation()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("unused");
        var vault = new InMemorySecretVault();
        var expiresAtUnixMs = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeMilliseconds();
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
            auth));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Scheduled invocation agent key could not be resolved.");
        credentialExchange.Sources.Should().BeEmpty();
        invocationPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithAuthAndNonChatPayload_ShouldExchangeWithoutInjectingToken()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("sender-token-1");
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange);
        var auth = new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationIdentityCredentialSource(
            new ScheduledServiceInvocationIdentitySubject("lark", "tenant-1", "ou-user-1"),
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
        var auth = new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationIdentityCredentialSource(
            new ScheduledServiceInvocationIdentitySubject("lark", "tenant-1", "ou-user-1"),
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
        var auth = new ScheduledServiceInvocationAuth(CreateDurableCredentialReference());

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
        invokedChat.LlmControl.SenderCredentialRef.Should().BeEmpty();
        invokedChat.ConnectorHttpAuthorization.Should().BeEmpty();
        invokedChat.CallerDurableCredential.Should().NotBeNull();
        invokedChat.CallerDurableCredential.Purpose.Should().Be(CredentialSecretPurposes.WorkflowCallerDurableBearerToken);
        invokedChat.CallerDurableCredential.OwnerScopeKey.Should().Be("schedule:schedule-durable");
        invokedChat.CallerDurableCredential.SubjectId.Should().Be("credential-1");
        invokedChat.CallerDurableCredential.SourceKind.Should().Be(DurableCallerCredentialSourceKind.ScheduledDispatch);
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
        invokedChat.LlmControl.SenderCredentialRef.Should().Be("durable-run-key");
        invokedChat.LlmControl.CredentialRef.Should().BeEmpty();
        invokedChat.LlmControl.OrganizationCredentialRef.Should().BeEmpty();
        invokedChat.LlmControl.ModelOverride.Should().Be("opus");
        invokedChat.CallerDurableCredential.Should().BeNull();
        original.Payload.Unpack<ChatRequestEvent>().LlmControl.SenderCredentialRef.Should().BeEmpty();
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
        var auth = new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationIdentityCredentialSource(
            new ScheduledServiceInvocationIdentitySubject("lark", "tenant-1", "ou-user-1"),
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

        var result = await port.IssueAsync(CreateCredentialSource());

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("Scheduled service invocation sender identity credential exchange is not configured.");
    }

    [Fact]
    public async Task NyxIdScheduledServiceInvocationCredentialExchangePort_ShouldIssueTokenForScopeOwnerAndScope()
    {
        var broker = new RecordingCapabilityBroker { AccessToken = " owner-token " };
        var port = new NyxIdScheduledServiceInvocationCredentialExchangePort(
            broker,
            NullLogger<NyxIdScheduledServiceInvocationCredentialExchangePort>.Instance);

        var result = await port.IssueAsync(
            new ScheduledServiceInvocationIdentityCredentialSource(
                new ScheduledServiceInvocationIdentitySubject(OwnerScope.NyxIdPlatform, string.Empty, "owner-nyx-user"),
                "proxy",
                ScheduledServiceInvocationIdentityCredentialRole.ScopeOwner));

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

        var act = () => port.IssueAsync(
            new ScheduledServiceInvocationIdentityCredentialSource(
                null!,
                "proxy",
                ScheduledServiceInvocationIdentityCredentialRole.ScopeOwner));

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

        var result = await port.IssueAsync(CreateCredentialSource());

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

        var result = await port.IssueAsync(CreateCredentialSource());

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

        var act = () => port.IssueAsync(CreateCredentialSource(), cts.Token);

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

    private static ScheduledServiceInvocationIdentityCredentialSource CreateCredentialSource() =>
        new(
            new ScheduledServiceInvocationIdentitySubject("lark", "tenant-1", "ou-user-1"),
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
        public List<ScheduledServiceInvocationIdentityCredentialSource> Sources { get; } = [];
        public Task<ScheduledServiceInvocationCredentialExchangeResult> IssueAsync(
            ScheduledServiceInvocationIdentityCredentialSource source,
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
