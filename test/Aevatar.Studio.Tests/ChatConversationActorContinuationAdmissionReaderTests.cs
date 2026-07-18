using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatHistory;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Studio.Tests;

public sealed class ChatConversationActorContinuationAdmissionReaderTests
{
    [Fact]
    public async Task CanContinueAsync_ShouldDispatchTypedAdmissionToConversationActor()
    {
        var handledDispatch = new RecordingActorHandledDispatchPort();
        var admissionReader = new ChatConversationActorContinuationAdmissionReader(handledDispatch);

        var canContinue = await admissionReader.CanContinueAsync(
            " scope-alpha ",
            " conversation-alpha ");

        canContinue.Should().BeTrue();
        var call = handledDispatch.Calls.Should().ContainSingle().Which;
        call.ActorId.Should().Be(ChatHistoryActorIds.Conversation("scope-alpha", "conversation-alpha"));
        var request = call.Envelope.Payload.Unpack<ChatConversationContinuationAdmissionRequested>();
        request.ScopeId.Should().Be("scope-alpha");
        request.ConversationId.Should().Be("conversation-alpha");
        call.Envelope.Runtime.Should().NotBeNull();
        call.Envelope.Runtime!.Dispatch.Should().NotBeNull();
        call.Envelope.Runtime.Dispatch!.PropagateFailure.Should().BeTrue();
        call.Envelope.Runtime.Deduplication.Should().NotBeNull();
        call.Envelope.Runtime.Deduplication!.OperationId.Should()
            .StartWith("chat-conversation-continuation-admission:");
    }

    [Fact]
    public async Task CanContinueAsync_ShouldReturnFalse_WhenConversationActorIsMissing()
    {
        var handledDispatch = new RecordingActorHandledDispatchPort
        {
            Exception = new ActorNotFoundException(
                ChatHistoryActorIds.Conversation("scope-alpha", "conversation-missing")),
        };
        var admissionReader = new ChatConversationActorContinuationAdmissionReader(handledDispatch);

        var canContinue = await admissionReader.CanContinueAsync("scope-alpha", "conversation-missing");

        canContinue.Should().BeFalse();
    }

    [Fact]
    public async Task CanContinueAsync_ShouldReturnFalse_WhenConversationOwnerRejectsAdmission()
    {
        var handledDispatch = new RecordingActorHandledDispatchPort
        {
            Exception = new ChatConversationContinuationAdmissionNotFoundException(
                "scope-alpha",
                "conversation-deleted"),
        };
        var admissionReader = new ChatConversationActorContinuationAdmissionReader(handledDispatch);

        var canContinue = await admissionReader.CanContinueAsync("scope-alpha", "conversation-deleted");

        canContinue.Should().BeFalse();
    }

    private sealed class RecordingActorHandledDispatchPort : IActorHandledDispatchPort
    {
        public Exception? Exception { get; init; }

        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchHandledAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((actorId, envelope));
            if (Exception != null)
                return Task.FromException<DispatchAdmission>(Exception);

            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }
}
