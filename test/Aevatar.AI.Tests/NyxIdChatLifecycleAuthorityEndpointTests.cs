using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.AI.Tests;

public partial class NyxIdChatEndpointsCoverageTests
{
    [Fact]
    public async Task HandleCreateConversationAsync_WhenActorRegistrationFails_ShouldRequestCommittedCompensation()
    {
        var actorStore = new StubGAgentActorStore
        {
            AddActorException = new InvalidOperationException("registry unavailable"),
        };
        var runtime = new StubActorRuntime();

        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var actorId = AssertAcceptedCreateAck(response, "scope-a");
        actorStore.AddedActors.Should().BeEmpty();
        actorId.Should().Be(runtime.CreateCalls.Single().Id);
        await AssertSingleCreationUnavailableEventAsync(
            runtime,
            actorId,
            destroyActor: true,
            reason: "registration_failed");
        AssertSingleUnregistrationRequest(actorStore, "scope-a", actorId);
        actorStore.RemovedActors.Should().BeEmpty();
        runtime.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCreateConversationAsync_WhenRegistrationThrowsAfterCommit_ShouldRequestCommittedCompensation()
    {
        var actorStore = new StubGAgentActorStore
        {
            AddActorExceptionAfterCommit = new OperationCanceledException("cancelled during admission verification"),
        };
        var runtime = new StubActorRuntime();

        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var acceptedActorId = AssertAcceptedCreateAck(response, "scope-a");
        actorStore.AddedActors.Should().ContainSingle();
        var actorId = actorStore.AddedActors.Single().ActorId;
        acceptedActorId.Should().Be(actorId);
        await AssertSingleCreationUnavailableEventAsync(
            runtime,
            actorId,
            destroyActor: true,
            reason: "registration_failed");
        AssertSingleUnregistrationRequest(actorStore, "scope-a", actorId);
        actorStore.RemovedActors.Should().BeEmpty();
        runtime.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCreateConversationAsync_WhenRegistrationIsNotVisible_ShouldRequestCommittedCompensation()
    {
        var actorStore = new StubGAgentActorStore
        {
            RegisterStage = GAgentActorRegistryCommandStage.AcceptedForDispatch,
        };
        var runtime = new StubActorRuntime();

        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var acceptedActorId = AssertAcceptedCreateAck(response, "scope-a");
        actorStore.AddedActors.Should().ContainSingle();
        var actorId = actorStore.AddedActors.Single().ActorId;
        acceptedActorId.Should().Be(actorId);
        await AssertSingleCreationUnavailableEventAsync(
            runtime,
            actorId,
            destroyActor: true,
            reason: "registration_not_admission_visible");
        AssertSingleUnregistrationRequest(actorStore, "scope-a", actorId);
        actorStore.RemovedActors.Should().BeEmpty();
        runtime.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCreateConversationAsync_ShouldReturnAcceptedAck_AndNotDestroy_WhenRollbackCannotUnregister()
    {
        var actorStore = new StubGAgentActorStore
        {
            RegisterStage = GAgentActorRegistryCommandStage.AcceptedForDispatch,
            RemoveActorException = new InvalidOperationException("registry unavailable"),
        };
        var runtime = new StubActorRuntime();

        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var acceptedActorId = AssertAcceptedCreateAck(response, "scope-a");
        actorStore.AddedActors.Should().ContainSingle();
        var actorId = actorStore.AddedActors.Single().ActorId;
        acceptedActorId.Should().Be(actorId);
        await AssertSingleCreationUnavailableEventAsync(
            runtime,
            actorId,
            destroyActor: true,
            reason: "registration_not_admission_visible");
        AssertSingleUnregistrationRequest(actorStore, "scope-a", actorId);
        actorStore.RemovedActors.Should().BeEmpty();
        runtime.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCreateConversationAsync_WhenGAgentToolHintAndRegistrationNotVisible_ShouldRequestCommittedCompensation()
    {
        var actorStore = new StubGAgentActorStore
        {
            RegisterStage = GAgentActorRegistryCommandStage.AcceptedForDispatch,
        };
        var runtime = new StubActorRuntime();
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            GAgentToolHintAction("existing-agent-1"),
            []));

        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            queryPort,
            NewChatRouteResolver(),
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var actorId = AssertAcceptedCreateAck(response, "scope-a");
        actorId.Should().NotBe("existing-agent-1",
            "Refactor (issue1321-first): tool_choice_hint is tool prefill, not actor addressing");
        AssertSingleUnregistrationRequest(actorStore, "scope-a", actorId);
        actorStore.RemovedActors.Should().BeEmpty();
        runtime.DestroyCalls.Should().BeEmpty();
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(NyxIdChatConversationGAgent) &&
            call.Id == actorId);
    }

    [Fact]
    public async Task HandleCreateConversationAsync_WhenGAgentToolHintAndRegistrationThrows_ShouldRequestCommittedCompensation()
    {
        var actorStore = new StubGAgentActorStore
        {
            AddActorExceptionAfterCommit = new OperationCanceledException("cancelled during admission verification"),
        };
        var runtime = new StubActorRuntime();
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            GAgentToolHintAction("existing-agent-2"),
            []));

        var result = await InvokeResultAsync(
            "HandleCreateConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            actorStore,
            runtime,
            queryPort,
            NewChatRouteResolver(),
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        var actorId = AssertAcceptedCreateAck(response, "scope-a");
        actorId.Should().NotBe("existing-agent-2",
            "Refactor (issue1321-first): tool_choice_hint is tool prefill, not actor addressing");
        AssertSingleUnregistrationRequest(actorStore, "scope-a", actorId);
        actorStore.RemovedActors.Should().BeEmpty();
        runtime.DestroyCalls.Should().BeEmpty();
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(NyxIdChatConversationGAgent) &&
            call.Id == actorId);
    }

    [Fact]
    public async Task HandleDeleteConversationAsync_ShouldReturnOk_ThenFinishAfterCommittedCallbacks()
    {
        var actorStore = new StubGAgentActorStore();
        var historyCommandPort = new StubChatHistoryCommandPort();
        var runtime = new StubActorRuntime();
        var actor = await CreateActiveConversationAsync(
            runtime,
            actorStore,
            historyCommandPort,
            "scope-a",
            "actor-1");
        var result = await InvokeResultAsync(
            "HandleDeleteConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            "actor-1",
            runtime,
            actorStore,
            actorStore,
            historyCommandPort,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        AssertSingleUnregistrationRequest(actorStore, "scope-a", "actor-1");
        actorStore.RemovedActors.Should().BeEmpty();
        historyCommandPort.DeletedConversations.Should().BeEmpty();
        runtime.DestroyCalls.Should().BeEmpty();
        actorStore.AdmissionTargets.Should().ContainSingle(target =>
            target.ScopeId == "scope-a" &&
            target.ResourceKind == ScopeResourceKind.GAgentActor &&
            target.AgentKind == NyxIdChatServiceDefaults.GAgentKind &&
            target.ActorId == "actor-1" &&
            target.Operation == ScopeResourceOperation.Delete);

        await CommitRegistryUnregistrationAsync(
            actor,
            actorStore.UnregistrationRequests.Single());
        var historyRequest = historyCommandPort.DeletionRequests.Should().ContainSingle().Which;
        historyCommandPort.DeletedConversations.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" && entry.ConversationId == "actor-1");
        runtime.DestroyCalls.Should().BeEmpty();

        await CommitHistoryOwnerAsync(
            actor,
            historyRequest,
            ChatHistoryConversationOwnerKind.Canonical,
            ChatHistoryConversationDeletionOutcome.CommittedDeleted);
        await CommitHistoryOwnerAsync(
            actor,
            historyRequest,
            ChatHistoryConversationOwnerKind.Legacy,
            ChatHistoryConversationDeletionOutcome.AuthoritativeAbsent);

        runtime.DestroyCalls.Should().ContainSingle().Which.Should().Be("actor-1");
    }

    [Fact]
    public async Task HandleDeleteConversationAsync_WhenRegistryDispatchFails_ShouldRemainPendingAfterAcceptedAck()
    {
        var actorStore = new StubGAgentActorStore
        {
            RemoveActorException = new InvalidOperationException("registry unavailable"),
        };
        var historyCommandPort = new StubChatHistoryCommandPort();
        var runtime = new StubActorRuntime();
        var actor = await CreateActiveConversationAsync(
            runtime,
            actorStore,
            historyCommandPort,
            "scope-a",
            "actor-1");

        var result = await InvokeResultAsync(
            "HandleDeleteConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            "actor-1",
            runtime,
            actorStore,
            actorStore,
            historyCommandPort,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        AssertSingleUnregistrationRequest(actorStore, "scope-a", "actor-1");
        historyCommandPort.DeletedConversations.Should().BeEmpty();
        runtime.DestroyCalls.Should().BeEmpty();
        var lifecycle = ((NyxIdChatConversationGAgent)actor.Agent).State.ConversationLifecycle;
        lifecycle.Phase.Should().Be(NyxIdChatConversationLifecyclePhase.DeletionUnregisterPending);
        lifecycle.LastFailureCode.Should().Be("deletion_unregister_failed");
    }

    [Fact]
    public async Task HandleDeleteConversationAsync_WhenHistoryDispatchFails_ShouldStayPendingWithoutCompensation()
    {
        var actorStore = new StubGAgentActorStore();
        var historyCommandPort = new StubChatHistoryCommandPort
        {
            DeleteConversationException = new InvalidOperationException("history unavailable"),
        };
        var runtime = new StubActorRuntime();
        var actor = await CreateActiveConversationAsync(
            runtime,
            actorStore,
            historyCommandPort,
            "scope-a",
            "actor-1");

        var result = await InvokeResultAsync(
            "HandleDeleteConversationAsync",
            new DefaultHttpContext(),
            "scope-a",
            "actor-1",
            runtime,
            actorStore,
            actorStore,
            historyCommandPort,
            CancellationToken.None);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        await CommitRegistryUnregistrationAsync(
            actor,
            actorStore.UnregistrationRequests.Should().ContainSingle().Which);

        historyCommandPort.DeletionRequests.Should().ContainSingle();
        actorStore.RemovedActors.Should().BeEmpty();
        actorStore.AddedActors.Should().ContainSingle(entry =>
            entry.ScopeId == "scope-a" &&
            entry.AgentKind == NyxIdChatServiceDefaults.GAgentKind &&
            entry.ActorId == "actor-1");
        runtime.DestroyCalls.Should().BeEmpty();
        var lifecycle = ((NyxIdChatConversationGAgent)actor.Agent).State.ConversationLifecycle;
        lifecycle.Phase.Should().Be(NyxIdChatConversationLifecyclePhase.DeletionHistoryDeletePending);
        lifecycle.LastFailureCode.Should().Be("history_delete_dispatch_ambiguous");
    }
}
