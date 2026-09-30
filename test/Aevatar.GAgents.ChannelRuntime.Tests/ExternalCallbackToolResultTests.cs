using Aevatar.GAgents.Channel.Abstractions;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed partial class ExternalCallbackGAgentTests
{
    [Fact]
    public async Task ToolResult_WaitsForOriginalRunAndContext_ThenRetriesUntilDurableConsumption()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(linkDeliveryMode: ExternalCallbackLinkDeliveryMode.Tool);
        await h.AttachAsync();
        h.Agent.State.Registration.Should().NotBeNull();
        h.Agent.State.ToolResult.Should().BeNull();
        h.Creator.Calls.Should().Be(0, "admission alone cannot orphan an external link before the run commits its pending call");
        h.Publisher.Sent.OfType<ConnectLinkToolResultProduced>().Should().BeEmpty();
        await h.DeliverAsync(ToolRequest(), "run-actor-a");
        await h.AttachAsync();
        h.Agent.State.ToolResult.ConnectUrl.Should().Be(h.Agent.State.LinkUrl);
        h.Publisher.Sent.OfType<ConnectLinkToolResultProduced>().Should().BeEmpty("the callback route must be durable first");
        await h.DeliverAsync(new ExternalCallbackActionContextAccepted { CallbackId = "cb-a", ContextRevision = 1 });

        var result = h.Publisher.Sent.OfType<ConnectLinkToolResultProduced>().Should().ContainSingle().Subject;
        result.Target.Should().Be(ToolTarget());
        result.Result.ConnectUrl.Should().Be("https://nyxid.test/connect/link-exact");
        result.Result.ExternalRequestId.Should().Be("link-exact");
        h.Publisher.Sent.OfType<ExternalCallbackLinkReady>().Should().BeEmpty();

        await h.ReactivateAsync();
        await h.FireAsync();
        h.Publisher.Sent.OfType<ConnectLinkToolResultProduced>().Should().HaveCount(2);
        await h.DeliverAsync(ToolConsumed(), "run-actor-a");
        await h.ReactivateAsync();
        await h.FireAsync();
        h.Publisher.Sent.OfType<ConnectLinkToolResultProduced>().Should().HaveCount(2);
        h.Agent.State.ToolResultConsumed.Should().BeTrue();
        h.Agent.State.LinkPresented.Should().BeFalse("receiving a tool result is not user-visible presentation");
        h.Creator.Calls.Should().Be(1);

        h.Verifier.Result = Success();
        await h.FireAsync();
        await h.DeliverAsync(new ExternalCallbackActionContextAccepted
        { CallbackId = "cb-a", ContextRevision = h.Agent.State.ContextRevision });
        h.Publisher.Sent.OfType<CallbackCompleted>().Should().ContainSingle();
    }

    [Theory]
    [InlineData("publisher")]
    [InlineData("run")]
    [InlineData("call")]
    [InlineData("attempt")]
    [InlineData("step")]
    [InlineData("callback")]
    [InlineData("operation")]
    public async Task ToolResult_RejectsMismatchedReadyAndConsumedIdentities(string mismatch)
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(linkDeliveryMode: ExternalCallbackLinkDeliveryMode.Tool);
        await h.AttachAsync();
        await h.DeliverAsync(new ExternalCallbackActionContextAccepted { CallbackId = "cb-a", ContextRevision = 1 });
        var request = ToolRequest();
        switch (mismatch)
        {
            case "run": request.Target.RunId = "run-other"; break;
            case "call": request.Target.CallId = "call-other"; break;
            case "attempt": request.Target.Attempt++; break;
            case "step": request.Target.StepIndex++; break;
            case "callback": request.CallbackId = "cb-other"; break;
            case "operation": request.OperationActorId = "operation-other"; break;
        }
        var publisher = mismatch == "publisher" ? "actor-other" : "run-actor-a";
        await h.DeliverAsync(request, publisher);
        h.Agent.State.ToolResultRequested.Should().BeFalse();
        h.Publisher.Sent.OfType<ConnectLinkToolResultProduced>().Should().BeEmpty();
        await h.DeliverAsync(ToolRequest(), "run-actor-a");
        await h.AttachAsync();
        await h.DeliverAsync(new ConnectLinkToolResultConsumed
        { CallbackId = request.CallbackId, OperationActorId = request.OperationActorId, Target = request.Target }, publisher);
        h.Agent.State.ToolResultConsumed.Should().BeFalse();
    }

    [Fact]
    public async Task ToolRunRejection_StopsUndeliverableResultWithoutInventingProviderFailureOrBusinessReply()
    {
        using var h = await Harness.CreateAsync();
        await h.RegisterAsync(linkDeliveryMode: ExternalCallbackLinkDeliveryMode.Tool);
        await h.DeliverAsync(ToolRequest(), "run-actor-a");
        await h.AttachAsync();
        var result = h.Agent.State.ToolResult.Clone();
        await h.DeliverAsync(new ConnectLinkToolResultRejected
        {
            CallbackId = "cb-a", OperationActorId = "opaque-operation-a", Target = ToolTarget(),
            FailureCode = "connect_link_creation_timed_out",
        }, "run-actor-a");
        await h.ReactivateAsync();
        h.Verifier.Result = Success();
        await h.FireAsync();

        h.Agent.State.ToolResult.Should().Be(result, "a local timeout does not rewrite the provider creation result");
        h.Agent.State.ToolResultConsumed.Should().BeFalse();
        h.Agent.State.ConsumedAtUnixMs.Should().BeGreaterThan(0);
        h.Agent.State.DeliveryFailureCode.Should().Be("connect_link_creation_timed_out");
        h.Publisher.Sent.OfType<CallbackCompleted>().Should().BeEmpty();
        h.Publisher.Sent.OfType<ExternalCallbackLinkReady>().Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCreationFailure_CompletesOriginalToolWithoutStartingAnotherBusinessReply()
    {
        using var h = await Harness.CreateAsync();
        h.Creator.FailureCode = "sender_authorization_unavailable";
        await h.RegisterAsync(linkDeliveryMode: ExternalCallbackLinkDeliveryMode.Tool);
        await h.DeliverAsync(ToolRequest(), "run-actor-a");
        await h.AttachAsync();
        h.Agent.State.ToolResult.FailureCode.Should().NotBeNullOrWhiteSpace();
        h.Agent.State.ToolResult.FailureOutcome.Should().Be(ConnectLinkCreationFailureOutcome.NotCreated);
        h.Publisher.Sent.OfType<ConnectLinkToolResultProduced>().Should().ContainSingle();
        await h.DeliverAsync(new ExternalCallbackActionContextAccepted
        { CallbackId = "cb-a", ContextRevision = h.Agent.State.ContextRevision });
        await h.DeliverAsync(ToolConsumed(), "run-actor-a");
        await h.ReactivateAsync();
        await h.FireAsync();

        h.Agent.State.ConsumedAtUnixMs.Should().BeGreaterThan(0);
        h.Publisher.Sent.OfType<CallbackCompleted>().Should().BeEmpty();
        h.Publisher.Sent.OfType<ExternalCallbackLinkReady>().Should().BeEmpty();
    }

    private static ConnectLinkToolContinuationTarget ToolTarget() => new()
    { ActorId = "run-actor-a", RunId = "run-a", Attempt = 1, StepIndex = 3, CallId = "action-a" };

    private static ConnectLinkToolResultRequested ToolRequest() => new()
    { CallbackId = "cb-a", OperationActorId = "opaque-operation-a", Target = ToolTarget() };

    private static ConnectLinkToolResultConsumed ToolConsumed() => new()
    { CallbackId = "cb-a", OperationActorId = "opaque-operation-a", Target = ToolTarget() };
}
