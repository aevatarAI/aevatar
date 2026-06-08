using Aevatar.AI.Abstractions;
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
                Metadata = { ["trace"] = "kept" },
                LlmControl = new LLMControlContextPayload
                {
                    ModelOverride = "sonnet",
                },
            }),
        };
        var auth = new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
            new ScheduledServiceInvocationNyxIdSubjectRef("lark", "tenant-1", "ou-user-1"),
            "proxy"));

        await port.DispatchAsync(new ScheduledServiceInvocationDispatchRequest(original, auth));

        credentialExchange.Sources.Should().ContainSingle()
            .Which.Subject.ExternalUserId.Should().Be("ou-user-1");
        var invoked = invocationPort.Requests.Should().ContainSingle().Which;
        invoked.Should().NotBeSameAs(original);
        var invokedChat = invoked.Payload.Unpack<ChatRequestEvent>();
        invokedChat.LlmControl.SenderNyxIdAccessToken.Should().Be("sender-token-1");
        invokedChat.LlmControl.ModelOverride.Should().Be("sonnet");
        invokedChat.Metadata.Should().Contain("trace", "kept");
        invokedChat.Metadata.Should().NotContainValue("sender-token-1");
        original.Payload.Unpack<ChatRequestEvent>().LlmControl.SenderNyxIdAccessToken.Should().BeEmpty();
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

    private sealed class RecordingScheduledServiceInvocationCredentialExchangePort(
        string? accessToken = null,
        string? error = null) : IScheduledServiceInvocationCredentialExchangePort
    {
        public List<ScheduledServiceInvocationNyxIdCredentialSource> Sources { get; } = [];

        public Task<ScheduledServiceInvocationCredentialExchangeResult> IssueSenderNyxIdAsync(
            ScheduledServiceInvocationNyxIdCredentialSource source,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Sources.Add(source);
            return Task.FromResult(error == null
                ? ScheduledServiceInvocationCredentialExchangeResult.Success(accessToken ?? "sender-token")
                : ScheduledServiceInvocationCredentialExchangeResult.Failure(error));
        }
    }
}
