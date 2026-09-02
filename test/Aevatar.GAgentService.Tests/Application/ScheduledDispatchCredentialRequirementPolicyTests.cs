using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Application.Schedules;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScheduledDispatchCredentialRequirementPolicyTests
{
    private readonly DefaultScheduledDispatchCredentialRequirementPolicy _policy = new();

    [Theory]
    [InlineData(ScheduledDispatchCredentialRequirementTargetKind.Envelope)]
    [InlineData(ScheduledDispatchCredentialRequirementTargetKind.StaticService)]
    [InlineData(ScheduledDispatchCredentialRequirementTargetKind.ScriptingService)]
    [InlineData(ScheduledDispatchCredentialRequirementTargetKind.Unspecified)]
    public void Evaluate_ShouldAllowNoAuth_ForOptionalTargets(
        ScheduledDispatchCredentialRequirementTargetKind targetKind)
    {
        var decision = _policy.Evaluate(CreateRequest(targetKind));

        decision.Allowed.Should().BeTrue();
        decision.CredentialRequired.Should().BeFalse();
    }

    [Theory]
    [InlineData(ScheduledDispatchCredentialRequirementTargetKind.WorkflowService, ScheduledDispatchCredentialRequirementOperation.Create)]
    [InlineData(ScheduledDispatchCredentialRequirementTargetKind.WorkflowService, ScheduledDispatchCredentialRequirementOperation.Update)]
    [InlineData(ScheduledDispatchCredentialRequirementTargetKind.Connector, ScheduledDispatchCredentialRequirementOperation.Create)]
    [InlineData(ScheduledDispatchCredentialRequirementTargetKind.Connector, ScheduledDispatchCredentialRequirementOperation.Update)]
    public void Evaluate_ShouldRejectNoAuth_ForRequiredTargetsOnConfigure(
        ScheduledDispatchCredentialRequirementTargetKind targetKind,
        ScheduledDispatchCredentialRequirementOperation operation)
    {
        var decision = _policy.Evaluate(CreateRequest(targetKind, operation: operation));

        decision.Allowed.Should().BeFalse();
        decision.CredentialRequired.Should().BeTrue();
        decision.ViolationCode.Should().Be(ScheduledDispatchCredentialViolationCode.CredentialRequired);
        decision.Message.Should().Contain("requires a typed service invocation credential source");
    }

    [Theory]
    [InlineData(ScheduledDispatchCredentialSourceKind.SenderNyxId)]
    [InlineData(ScheduledDispatchCredentialSourceKind.ScopeOwnerNyxId)]
    [InlineData(ScheduledDispatchCredentialSourceKind.DurableCredentialReference)]
    [InlineData(ScheduledDispatchCredentialSourceKind.ScheduledInvocationAgentKey)]
    public void Evaluate_ShouldAllowTypedCredentialSources_ForRequiredTargets(
        ScheduledDispatchCredentialSourceKind credentialSourceKind)
    {
        var decision = _policy.Evaluate(CreateRequest(
            ScheduledDispatchCredentialRequirementTargetKind.WorkflowService,
            credentialSourceKind));

        decision.Allowed.Should().BeTrue();
        decision.CredentialRequired.Should().BeTrue();
    }

    [Theory]
    [InlineData(ScheduledDispatchCredentialSourceKind.LegacyDurableSenderBearer)]
    [InlineData(ScheduledDispatchCredentialSourceKind.Multiple)]
    public void Evaluate_ShouldRejectUnsupportedCredentialSources(
        ScheduledDispatchCredentialSourceKind credentialSourceKind)
    {
        var decision = _policy.Evaluate(CreateRequest(
            ScheduledDispatchCredentialRequirementTargetKind.StaticService,
            credentialSourceKind));

        decision.Allowed.Should().BeFalse();
        decision.ViolationCode.Should().NotBe(ScheduledDispatchCredentialViolationCode.None);
    }

    [Fact]
    public void Evaluate_ShouldRejectCurrentSessionCredentialSignal()
    {
        var decision = _policy.Evaluate(CreateRequest(
            ScheduledDispatchCredentialRequirementTargetKind.StaticService,
            ScheduledDispatchCredentialSourceKind.None,
            new ScheduledDispatchPayloadCredentialSignal(true, nameof(ChatRequestEvent.LlmControl))));

        decision.Allowed.Should().BeFalse();
        decision.ViolationCode.Should().Be(ScheduledDispatchCredentialViolationCode.CurrentSessionCredential);
    }

    [Fact]
    public void SummarizePayloadCredentialSignal_ShouldDetectConnectorHttpAuthorizationPayload()
    {
        var signal = ScheduledDispatchCredentialRequirementRequests.SummarizePayloadCredentialSignal(
            Any.Pack(new ChatRequestEvent
            {
                ConnectorHttpAuthorization = "Bearer current-session-token",
            }));

        signal.HasCurrentSessionCredential.Should().BeTrue();
        signal.Source.Should().Be(nameof(ChatRequestEvent.ConnectorHttpAuthorization));
    }

    [Fact]
    public void SummarizePayloadCredentialSignal_ShouldDetectCallerSourceReadableBearerPayload()
    {
        var signal = ScheduledDispatchCredentialRequirementRequests.SummarizePayloadCredentialSignal(
            Any.Pack(new ChatRequestEvent
            {
                CallerSourceReadableNyxIdBearerToken = "current-session-source-token",
            }));

        signal.HasCurrentSessionCredential.Should().BeTrue();
        signal.Source.Should().Be(nameof(ChatRequestEvent.CallerSourceReadableNyxIdBearerToken));
    }

    [Fact]
    public void SummarizePayloadCredentialSignal_ShouldDetectToolContextCredentialsPayload()
    {
        var signal = ScheduledDispatchCredentialRequirementRequests.SummarizePayloadCredentialSignal(
            Any.Pack(new ChatRequestEvent
            {
                ToolContext = new AgentToolExecutionContextPayload
                {
                    Credentials = new AgentToolCredentialsPayload
                    {
                        SourceReadableNyxIdAccessToken = "source-token",
                    },
                },
            }));

        signal.HasCurrentSessionCredential.Should().BeTrue();
        signal.Source.Should().Be("ToolContext.Credentials");
    }

    [Fact]
    public void FromConfiguration_ShouldDetectCredentialBearingPayloadAndHeaders()
    {
        var request = ScheduledDispatchCredentialRequirementRequests.FromConfiguration(
            new ScheduledDispatchConfiguration(
                "schedule-1",
                string.Empty,
                new ScheduledDispatchTargetDescriptor(
                    ScheduledDispatchTargetKind.ServiceInvocation,
                    ServiceInvocation: new ScheduledServiceInvocationTargetDescriptor(
                        new ServiceIdentity { TenantId = "tenant", AppId = "app", Namespace = "default", ServiceId = "svc" },
                        "chat",
                        Any.Pack(new ChatRequestEvent
                        {
                            LlmControl = new LLMControlContextPayload
                            {
                                SenderNyxIdAccessToken = "sender-token",
                            },
                        }))),
                "0 9 * * *",
                "UTC",
                true,
                new Dictionary<string, string>()),
            ScheduledDispatchCredentialRequirementOperation.Create);

        request.PayloadCredentialSignal.HasCurrentSessionCredential.Should().BeTrue();
        request.PayloadCredentialSignal.Source.Should().Be(nameof(ChatRequestEvent.LlmControl));

        var headerRequest = ScheduledDispatchCredentialRequirementRequests.FromConfiguration(
            new ScheduledDispatchConfiguration(
                "schedule-2",
                string.Empty,
                new ScheduledDispatchTargetDescriptor(
                    ScheduledDispatchTargetKind.Envelope,
                    ActorId: "actor-1",
                    Envelope: new EventEnvelope { Payload = Any.Pack(new Empty()) }),
                "0 9 * * *",
                "UTC",
                true,
                new Dictionary<string, string>
                {
                    [ScheduledDispatchCredentialRequirementRequests.LegacyConnectorHttpAuthorizationHeader] = "Bearer current",
                }),
            ScheduledDispatchCredentialRequirementOperation.Create);

        headerRequest.PayloadCredentialSignal.HasCurrentSessionCredential.Should().BeTrue();
        headerRequest.PayloadCredentialSignal.Source.Should().Be(
            ScheduledDispatchCredentialRequirementRequests.LegacyConnectorHttpAuthorizationHeader);
    }

    private static ScheduledDispatchCredentialRequirementRequest CreateRequest(
        ScheduledDispatchCredentialRequirementTargetKind targetKind,
        ScheduledDispatchCredentialSourceKind credentialSourceKind = ScheduledDispatchCredentialSourceKind.None,
        ScheduledDispatchPayloadCredentialSignal? payloadCredentialSignal = null,
        ScheduledDispatchCredentialRequirementOperation operation = ScheduledDispatchCredentialRequirementOperation.Create) =>
        new(
            "schedule-1",
            operation,
            ScheduledDispatchScheduleKind.Generic,
            targetKind,
            new ScheduledDispatchCredentialSourceSummary(credentialSourceKind),
            payloadCredentialSignal ?? ScheduledDispatchPayloadCredentialSignal.None);
}
