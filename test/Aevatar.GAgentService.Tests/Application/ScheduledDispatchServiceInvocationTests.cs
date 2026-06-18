using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Application.Schedules;
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
                        ToolContext = new AgentToolExecutionContextPayload
                        {
                            Credentials = new AgentToolCredentialsPayload
                            {
                                NyxIdAccessToken = "tool-owner-secret",
                                NyxIdOrgToken = "tool-org-secret",
                                SenderNyxIdAccessToken = "tool-sender-secret",
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
        persistedChat.LlmControl.Should().NotBeNull();
        persistedChat.LlmControl.NyxIdAccessToken.Should().BeEmpty();
        persistedChat.LlmControl.NyxIdOrgToken.Should().BeEmpty();
        persistedChat.LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
        persistedChat.LlmControl.ModelOverride.Should().Be("sonnet");
        persistedChat.LlmControl.NyxIdRoutePreference.Should().Be("low-latency");
        persistedChat.LlmControl.MaxToolRoundsOverride.Should().Be(3);
        persistedChat.LlmControl.UserMemoryPrompt.Should().Be("remember preferences");
        persistedChat.ToolContext.Credentials.NyxIdAccessToken.Should().BeEmpty();
        persistedChat.ToolContext.Credentials.NyxIdOrgToken.Should().BeEmpty();
        persistedChat.ToolContext.Credentials.SenderNyxIdAccessToken.Should().BeEmpty();
        persistedChat.ToolContext.Routing.ModelOverride.Should().Be("tool-model");
        persistedChat.ToolContext.Routing.NyxIdRoutePreference.Should().Be("tool-route");
        persistedChat.ToolContext.Routing.MaxToolRoundsOverride.Should().Be(5);
        persistedChat.ToolContext.Routing.UserMemoryPrompt.Should().Be("tool memory");
        var descriptorChat = prepared.Descriptor.ServiceInvocation!.Payload.Unpack<ChatRequestEvent>();
        descriptorChat.LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
        descriptorChat.LlmControl.ModelOverride.Should().Be("sonnet");
        descriptorChat.ToolContext.Credentials.SenderNyxIdAccessToken.Should().BeEmpty();
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
                }));

        invocationPort.Requests.Should().ContainSingle()
            .Which.Payload.Unpack<StringValue>().Value.Should().Be("invoke");
        credentialExchange.Sources.Should().BeEmpty();
        receipt.Accepted.Should().BeTrue();
        receipt.CommandId.Should().Be("cmd-invoke");
        receipt.CorrelationId.Should().Be("corr-invoke");
        receipt.TargetActorId.Should().Be("service-actor");
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithScopeOwnerAuth_ShouldExchangeOwnerTokenWithoutRequestSubject()
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

        credentialExchange.Sources.Should().BeEmpty();
        credentialExchange.ScopeOwnerSources.Should().ContainSingle()
            .Which.Scope.Should().Be("proxy");
        credentialExchange.ScopeOwnerServiceIdentities.Should().ContainSingle()
            .Which.TenantId.Should().Be("owner-nyx-user");
        var invokedChat = invocationPort.Requests.Should().ContainSingle().Which.Payload.Unpack<ChatRequestEvent>();
        invokedChat.LlmControl.SenderNyxIdAccessToken.Should().Be("owner-token");
    }

    [Fact]
    public async Task ScheduledServiceInvocationDispatchPort_WithAuth_ShouldExchangeAndInjectSenderTokenIntoClonedChatPayload()
    {
        var invocationPort = new RecordingServiceInvocationPort();
        var credentialExchange = new RecordingScheduledServiceInvocationCredentialExchangePort("sender-token-1");
        var port = new ScheduledServiceInvocationDispatchPort(invocationPort, credentialExchange);
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
            "proxy"));

        await port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(
            original,
            auth,
            new Dictionary<string, string>
            {
                ["connector.http.authorization"] = "Bearer header-token",
                ["schedule"] = "scheduled",
            },
            ProjectSenderNyxIdAccessTokenToWorkflowCallerCredential: true));

        credentialExchange.Sources.Should().ContainSingle()
            .Which.Subject.ExternalUserId.Should().Be("ou-user-1");
        var invoked = invocationPort.Requests.Should().ContainSingle().Which;
        invoked.Should().NotBeSameAs(original);
        var invokedChat = invoked.Payload.Unpack<ChatRequestEvent>();
        invokedChat.LlmControl.SenderNyxIdAccessToken.Should().Be("sender-token-1");
        invokedChat.LlmControl.ModelOverride.Should().Be("sonnet");
        invokedChat.ConnectorHttpAuthorization.Should().Be("Bearer sender-token-1");
        invokedChat.Metadata.Should().Contain("trace", "kept");
        invokedChat.Metadata.Should().NotContainKey("connector.http.authorization");
        invokedChat.Metadata.Should().Contain("schedule", "scheduled");
        invokedChat.Metadata.Should().NotContainValue("sender-token-1");
        invokedChat.Metadata.Should().NotContainValue("Bearer sender-token-1");
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

        var result = await port.IssueSenderNyxIdAsync(CreateCredentialSource());

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

        var result = await port.IssueScopeOwnerNyxIdAsync(
            new ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource("proxy"),
            new ServiceIdentity { TenantId = "owner-nyx-user", ServiceId = "svc" });

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
    public async Task NyxIdScheduledServiceInvocationCredentialExchangePort_ShouldIssueTokenForSubjectAndScope()
    {
        var broker = new RecordingCapabilityBroker { AccessToken = " sender-token " };
        var port = new NyxIdScheduledServiceInvocationCredentialExchangePort(
            broker,
            NullLogger<NyxIdScheduledServiceInvocationCredentialExchangePort>.Instance);

        var result = await port.IssueSenderNyxIdAsync(CreateCredentialSource());

        result.Succeeded.Should().BeTrue();
        result.AccessToken.Should().Be(" sender-token ");
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
    [InlineData("empty-token")]
    [InlineData("unexpected")]
    public async Task NyxIdScheduledServiceInvocationCredentialExchangePort_ShouldMapBrokerFailures(string failure)
    {
        var broker = new RecordingCapabilityBroker { Failure = failure };
        var port = new NyxIdScheduledServiceInvocationCredentialExchangePort(
            broker,
            NullLogger<NyxIdScheduledServiceInvocationCredentialExchangePort>.Instance);

        var result = await port.IssueSenderNyxIdAsync(CreateCredentialSource());

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(failure switch
        {
            "not-found" => "NyxID binding was not found for the scheduled subject.",
            "revoked" => "NyxID binding was revoked for the scheduled subject.",
            "scope" => "NyxID binding does not grant the requested schedule scope.",
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

        var act = () => port.IssueSenderNyxIdAsync(CreateCredentialSource(), cts.Token);

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

    private static ScheduledServiceInvocationNyxIdCredentialSource CreateCredentialSource() =>
        new(
            new ScheduledServiceInvocationNyxIdSubjectRef("lark", "tenant-1", "ou-user-1"),
            "proxy");

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

    private sealed class RecordingScheduledServiceInvocationCredentialExchangePort(
        string? accessToken = null,
        string? error = null) : IScheduledServiceInvocationCredentialExchangePort
    {
        public List<ScheduledServiceInvocationNyxIdCredentialSource> Sources { get; } = [];
        public List<ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource> ScopeOwnerSources { get; } = [];
        public List<ServiceIdentity> ScopeOwnerServiceIdentities { get; } = [];

        public Task<ScheduledServiceInvocationCredentialExchangeResult> IssueSenderNyxIdAsync(
            ScheduledServiceInvocationNyxIdCredentialSource source,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Sources.Add(source);
            return Task.FromResult(CreateResult());
        }

        public Task<ScheduledServiceInvocationCredentialExchangeResult> IssueScopeOwnerNyxIdAsync(
            ScheduledServiceInvocationScopeOwnerNyxIdCredentialSource source,
            ServiceIdentity serviceIdentity,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ScopeOwnerSources.Add(source);
            ScopeOwnerServiceIdentities.Add(serviceIdentity.Clone());
            return Task.FromResult(CreateResult());
        }

        private ScheduledServiceInvocationCredentialExchangeResult CreateResult() =>
            error == null
                ? ScheduledServiceInvocationCredentialExchangeResult.Success(accessToken ?? "sender-token")
                : ScheduledServiceInvocationCredentialExchangeResult.Failure(error);
    }

    private sealed class RecordingCapabilityBroker : INyxIdCapabilityBroker
    {
        public string AccessToken { get; init; } = "sender-token";
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
                "unexpected" => throw new InvalidOperationException("broker failed"),
                "empty-token" => Task.FromResult(new CapabilityHandle()),
                _ => Task.FromResult(new CapabilityHandle { AccessToken = AccessToken }),
            };
        }
    }
}
