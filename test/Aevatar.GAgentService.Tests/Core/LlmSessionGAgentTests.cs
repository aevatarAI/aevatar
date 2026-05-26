using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class LlmSessionGAgentTests
{
    [Fact]
    public async Task HandleRegisterAsync_ShouldPersistRecord_AndDefaultStatusToAccepted()
    {
        var actor = CreateActor("resp_1");

        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });

        actor.State.Record.Should().NotBeNull();
        actor.State.Record!.ResponseId.Should().Be("resp_1");
        actor.State.Record.Status.Should().Be(LlmSessionStatus.Accepted);
        actor.State.Record.UpdatedAt.Should().NotBeNull();
        actor.State.LastAppliedEventVersion.Should().Be(1);
    }

    [Fact]
    public async Task HandleRegisterAsync_ShouldRejectOwnerMismatchOnReRegister()
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });
        var foreign = BuildRecord("resp_1");
        foreign.OwnerSubject = "user-2";

        var act = () => actor.HandleRegisterAsync(new RegisterResponseSessionRequested { Record = foreign });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*owner 'user-1'*cannot rebind to owner 'user-2'*");
    }

    [Fact]
    public async Task HandleUpdateStatusAsync_ShouldAdvanceStatus_AndStampCancellation()
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });

        await actor.HandleUpdateStatusAsync(new UpdateResponseSessionStatusRequested
        {
            ResponseId = "resp_1",
            Status = LlmSessionStatus.Cancelled,
        });

        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Cancelled);
        actor.State.Record.CancelledAt.Should().NotBeNull();
        actor.State.LastAppliedEventVersion.Should().Be(2);
    }

    [Fact]
    public async Task HandleUpdateStatusAsync_ShouldRejectWhenNotRegistered()
    {
        var actor = CreateActor("resp_1");

        var act = () => actor.HandleUpdateStatusAsync(new UpdateResponseSessionStatusRequested
        {
            ResponseId = "resp_1",
            Status = LlmSessionStatus.Completed,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*has no registered response*");
    }

    [Fact]
    public async Task HandleRecordForwardedToolCallAsync_ShouldPersistPendingCall()
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });

        await actor.HandleRecordForwardedToolCallAsync(new RecordForwardedToolCallRequested
        {
            ResponseId = "resp_1",
            Call = BuildToolCall("call_1"),
        });

        actor.State.ForwardedToolCalls.Should().ContainSingle();
        var call = actor.State.ForwardedToolCalls[0];
        call.CallId.Should().Be("call_1");
        call.ToolName.Should().Be("get_weather");
        call.SchemaHash.Should().Be("schema-1");
        ResponsesJsonValues.ToBoundaryJson(call.Arguments).Should().Be("""{"city":"Singapore"}""");
        call.Status.Should().Be(LlmSessionForwardedToolCallStatus.Pending);
        call.Expiry.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleReceiveForwardedToolResultAsync_ShouldPersistResult_AndIgnoreDuplicate()
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });
        await actor.HandleRecordForwardedToolCallAsync(new RecordForwardedToolCallRequested
        {
            ResponseId = "resp_1",
            Call = BuildToolCall("call_1"),
        });

        await actor.HandleReceiveForwardedToolResultAsync(new ReceiveForwardedToolResultRequested
        {
            ResponseId = "resp_1",
            CallId = "call_1",
            SchemaHash = "schema-1",
            Result = ResponsesJsonValues.ParseBoundaryPayload("""{"temperature":28}"""),
        });
        var versionAfterFirstResult = actor.State.LastAppliedEventVersion;

        await actor.HandleReceiveForwardedToolResultAsync(new ReceiveForwardedToolResultRequested
        {
            ResponseId = "resp_1",
            CallId = "call_1",
            SchemaHash = "schema-1",
            Result = ResponsesJsonValues.ParseBoundaryPayload("""{"temperature":28}"""),
        });

        actor.State.LastAppliedEventVersion.Should().Be(versionAfterFirstResult);
        var call = actor.State.ForwardedToolCalls.Should().ContainSingle().Which;
        call.Status.Should().Be(LlmSessionForwardedToolCallStatus.Received);
        ResponsesJsonValues.ToBoundaryJson(call.Result).Should().Be("""{"temperature":28}""");
        call.ReceivedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleRecordForwardedToolCallAsync_ShouldRejectRebindingToDifferentArguments()
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });
        await actor.HandleRecordForwardedToolCallAsync(new RecordForwardedToolCallRequested
        {
            ResponseId = "resp_1",
            Call = BuildToolCall("call_1"),
        });

        var rebound = BuildToolCall("call_1");
        rebound.Arguments = ResponsesJsonValues.ParseBoundaryPayload("""{"city":"Tokyo"}""");
        var act = () => actor.HandleRecordForwardedToolCallAsync(new RecordForwardedToolCallRequested
        {
            ResponseId = "resp_1",
            Call = rebound,
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be rebound to different tool call facts*");
    }

    [Fact]
    public async Task HandleResolveForwardedToolResultAsync_ShouldMarkResultResolved_AndIgnoreDuplicate()
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });
        await actor.HandleRecordForwardedToolCallAsync(new RecordForwardedToolCallRequested
        {
            ResponseId = "resp_1",
            Call = BuildToolCall("call_1"),
        });
        await actor.HandleReceiveForwardedToolResultAsync(new ReceiveForwardedToolResultRequested
        {
            ResponseId = "resp_1",
            CallId = "call_1",
            SchemaHash = "schema-1",
            Result = ResponsesJsonValues.ParseBoundaryPayload("""{"temperature":28}"""),
        });

        await actor.HandleResolveForwardedToolResultAsync(new ResolveForwardedToolResultRequested
        {
            ResponseId = "resp_1",
            CallId = "call_1",
        });
        var versionAfterFirstResolve = actor.State.LastAppliedEventVersion;

        await actor.HandleResolveForwardedToolResultAsync(new ResolveForwardedToolResultRequested
        {
            ResponseId = "resp_1",
            CallId = "call_1",
        });

        actor.State.LastAppliedEventVersion.Should().Be(versionAfterFirstResolve);
        var call = actor.State.ForwardedToolCalls.Should().ContainSingle().Which;
        call.Status.Should().Be(LlmSessionForwardedToolCallStatus.Resolved);
        call.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleResolveForwardedToolResultAsync_WhenCallIsAlreadyResolved_ShouldPreserveFirstResolvedAt()
    {
        var actor = CreateActor("resp_1");
        var firstResolvedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-12T00:02:00+00:00"));
        var secondResolvedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-12T00:03:00+00:00"));

        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });
        await actor.HandleRecordForwardedToolCallAsync(new RecordForwardedToolCallRequested
        {
            ResponseId = "resp_1",
            Call = BuildToolCall("call_1"),
        });
        await actor.HandleReceiveForwardedToolResultAsync(new ReceiveForwardedToolResultRequested
        {
            ResponseId = "resp_1",
            CallId = "call_1",
            SchemaHash = "schema-1",
            Result = ResponsesJsonValues.ParseBoundaryPayload("""{"temperature":28}"""),
        });

        await actor.HandleResolveForwardedToolResultAsync(new ResolveForwardedToolResultRequested
        {
            ResponseId = "resp_1",
            CallId = "call_1",
            ResolvedAt = firstResolvedAt,
        });
        var versionAfterFirstResolve = actor.State.LastAppliedEventVersion;

        await actor.HandleResolveForwardedToolResultAsync(new ResolveForwardedToolResultRequested
        {
            ResponseId = "resp_1",
            CallId = "call_1",
            ResolvedAt = secondResolvedAt,
        });

        actor.State.LastAppliedEventVersion.Should().Be(versionAfterFirstResolve);
        var call = actor.State.ForwardedToolCalls.Should().ContainSingle().Which;
        call.Status.Should().Be(LlmSessionForwardedToolCallStatus.Resolved);
        call.ResolvedAt.Should().Be(firstResolvedAt);
        ResponsesJsonValues.ToBoundaryJson(call.Result).Should().Be("""{"temperature":28}""");
    }

    [Fact]
    public async Task ForwardedToolCallLifecycle_ShouldDurablyAdvanceFromPendingToReceivedToResolved()
    {
        var actor = CreateActor("resp_1");
        var emittedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-12T00:00:00+00:00"));
        var receivedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-12T00:01:00+00:00"));
        var resolvedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-12T00:02:00+00:00"));

        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });

        var call = BuildToolCall("call_1");
        call.EmittedAt = emittedAt.Clone();
        await actor.HandleRecordForwardedToolCallAsync(new RecordForwardedToolCallRequested
        {
            ResponseId = "resp_1",
            Call = call,
        });

        await actor.HandleReceiveForwardedToolResultAsync(new ReceiveForwardedToolResultRequested
        {
            ResponseId = "resp_1",
            CallId = "call_1",
            SchemaHash = "schema-1",
            Result = ResponsesJsonValues.ParseBoundaryPayload("""{"temperature":28}"""),
            ReceivedAt = receivedAt.Clone(),
        });

        await actor.HandleResolveForwardedToolResultAsync(new ResolveForwardedToolResultRequested
        {
            ResponseId = "resp_1",
            CallId = "call_1",
            ResolvedAt = resolvedAt.Clone(),
        });

        actor.State.LastAppliedEventVersion.Should().Be(4);
        actor.State.LastEventId.Should().Be("resp_1:tool:call_1:resolved");
        actor.State.ForwardedToolCalls.Should().ContainSingle();
        var persisted = actor.State.ForwardedToolCalls[0];
        persisted.Status.Should().Be(LlmSessionForwardedToolCallStatus.Resolved);
        persisted.EmittedAt.Should().Be(emittedAt);
        persisted.ReceivedAt.Should().Be(receivedAt);
        persisted.ResolvedAt.Should().Be(resolvedAt);
        ResponsesJsonValues.ToBoundaryJson(persisted.Result).Should().Be("""{"temperature":28}""");
    }

    [Fact]
    public async Task HandleReceiveForwardedToolResultAsync_ShouldRejectSchemaMismatch()
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });
        await actor.HandleRecordForwardedToolCallAsync(new RecordForwardedToolCallRequested
        {
            ResponseId = "resp_1",
            Call = BuildToolCall("call_1"),
        });

        var act = () => actor.HandleReceiveForwardedToolResultAsync(new ReceiveForwardedToolResultRequested
        {
            ResponseId = "resp_1",
            CallId = "call_1",
            SchemaHash = "schema-2",
            Result = ResponsesJsonValues.ParseBoundaryPayload("{}"),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*schema hash mismatch*");
    }

    [Fact]
    public async Task HandleUpdateStatusAsync_ShouldMarkPendingToolCallsCancelled()
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });
        await actor.HandleRecordForwardedToolCallAsync(new RecordForwardedToolCallRequested
        {
            ResponseId = "resp_1",
            Call = BuildToolCall("call_1"),
        });

        await actor.HandleUpdateStatusAsync(new UpdateResponseSessionStatusRequested
        {
            ResponseId = "resp_1",
            Status = LlmSessionStatus.Cancelled,
        });

        actor.State.ForwardedToolCalls.Should().ContainSingle()
            .Which.Status.Should().Be(LlmSessionForwardedToolCallStatus.Cancelled);
    }

    [Fact]
    public async Task HandleReceiveForwardedToolResultAsync_ShouldRejectCancelledCall_AndPreserveTerminalState()
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });
        await actor.HandleRecordForwardedToolCallAsync(new RecordForwardedToolCallRequested
        {
            ResponseId = "resp_1",
            Call = BuildToolCall("call_1"),
        });
        await actor.HandleUpdateStatusAsync(new UpdateResponseSessionStatusRequested
        {
            ResponseId = "resp_1",
            Status = LlmSessionStatus.Cancelled,
        });
        var versionAfterCancel = actor.State.LastAppliedEventVersion;

        var receive = () => actor.HandleReceiveForwardedToolResultAsync(new ReceiveForwardedToolResultRequested
        {
            ResponseId = "resp_1",
            CallId = "call_1",
            SchemaHash = "schema-1",
            Result = ResponsesJsonValues.ParseBoundaryPayload("""{"temperature":28}"""),
        });
        var resolve = () => actor.HandleResolveForwardedToolResultAsync(new ResolveForwardedToolResultRequested
        {
            ResponseId = "resp_1",
            CallId = "call_1",
        });

        await receive.Should().ThrowAsync<InvalidOperationException>();
        await resolve.Should().ThrowAsync<InvalidOperationException>();

        actor.State.LastAppliedEventVersion.Should().Be(versionAfterCancel);
        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Cancelled);
        actor.State.ForwardedToolCalls.Should().ContainSingle();
        var call = actor.State.ForwardedToolCalls[0];
        call.Status.Should().Be(LlmSessionForwardedToolCallStatus.Cancelled);
        call.Result.Should().BeNull();
        call.ResolvedAt.Should().BeNull();
    }

    [Fact]
    public async Task HandleRecordCompletionAsync_ShouldRecordCompletedFact()
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });

        await actor.HandleRecordCompletionAsync(new RecordResponseSessionCompletionRequested
        {
            ResponseId = "resp_1",
            Completion = BuildCompletion("done"),
        });

        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Completed);
        actor.State.Completion.Should().NotBeNull();
        actor.State.Completion!.OutputText.Should().Be("done");
        actor.State.Completion.ToolCalls.Should().ContainSingle()
            .Which.CallId.Should().Be("call_done");
        actor.State.LastAppliedEventVersion.Should().Be(2);
    }

    [Fact]
    public async Task HandleRecordCompletionAsync_ShouldRecordFailureFact()
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });

        await actor.HandleRecordCompletionAsync(new RecordResponseSessionCompletionRequested
        {
            ResponseId = "resp_1",
            Completion = new LlmSessionCompletion
            {
                FailureCode = "gagent_invocation_failed",
                FailureMessage = "GAgent invocation failed.",
            },
        });

        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Failed);
        actor.State.Completion!.FailureCode.Should().Be("gagent_invocation_failed");
        actor.State.Completion.FailureMessage.Should().Be("GAgent invocation failed.");
    }

    [Fact]
    public async Task HandleRecordCompletionAsync_ShouldIgnoreDuplicateSameFact()
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });
        await actor.HandleRecordCompletionAsync(new RecordResponseSessionCompletionRequested
        {
            ResponseId = "resp_1",
            Completion = BuildCompletion("done"),
        });
        var versionAfterFirstCompletion = actor.State.LastAppliedEventVersion;

        await actor.HandleRecordCompletionAsync(new RecordResponseSessionCompletionRequested
        {
            ResponseId = "resp_1",
            Completion = BuildCompletion("done"),
        });

        actor.State.LastAppliedEventVersion.Should().Be(versionAfterFirstCompletion);
    }

    [Fact]
    public async Task HandleRecordCompletionAsync_ShouldRejectDuplicateDifferentFact()
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });
        await actor.HandleRecordCompletionAsync(new RecordResponseSessionCompletionRequested
        {
            ResponseId = "resp_1",
            Completion = BuildCompletion("done"),
        });

        var act = () => actor.HandleRecordCompletionAsync(new RecordResponseSessionCompletionRequested
        {
            ResponseId = "resp_1",
            Completion = BuildCompletion("different"),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*completion cannot be rebound to different facts*");
    }

    [Fact]
    public async Task HandleRecordCompletionAsync_ShouldRejectInvalidCompletion()
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });

        var act = () => actor.HandleRecordCompletionAsync(new RecordResponseSessionCompletionRequested
        {
            ResponseId = "resp_1",
            Completion = new LlmSessionCompletion { FailureCode = "failed_without_message" },
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*failure_message is required*");
    }

    [Theory]
    [InlineData(LlmSessionStatus.Cancelled)]
    [InlineData(LlmSessionStatus.Expired)]
    public async Task HandleRecordCompletionAsync_ShouldRejectAfterTerminalNonCompletionStatus(LlmSessionStatus status)
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });
        if (status == LlmSessionStatus.Cancelled)
        {
            await actor.HandleUpdateStatusAsync(new UpdateResponseSessionStatusRequested
            {
                ResponseId = "resp_1",
                Status = LlmSessionStatus.Cancelled,
            });
        }
        else
        {
            var record = actor.State.Record!;
            await actor.HandleExpireResponseSessionAsync(new ExpireResponseSessionRequested
            {
                ResponseId = "resp_1",
                ObservedAt = Timestamp.FromDateTimeOffset(ResolveExpiry(record).AddSeconds(1)),
            });
        }

        var act = () => actor.HandleRecordCompletionAsync(new RecordResponseSessionCompletionRequested
        {
            ResponseId = "resp_1",
            Completion = BuildCompletion("late"),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*is {status} and cannot record completion*");
    }

    [Fact]
    public async Task HandleExpireResponseSessionAsync_ShouldExpirePendingToolCallsWithSyntheticError()
    {
        var actor = CreateActor("resp_1");
        var record = BuildRecord("resp_1");
        record.CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-2));
        record.Ttl = Duration.FromTimeSpan(TimeSpan.FromHours(1));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = record,
        });
        await actor.HandleRecordForwardedToolCallAsync(new RecordForwardedToolCallRequested
        {
            ResponseId = "resp_1",
            Call = BuildToolCall("call_1"),
        });

        await actor.HandleExpireResponseSessionAsync(new ExpireResponseSessionRequested
        {
            ResponseId = "resp_1",
            ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });

        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Expired);
        var call = actor.State.ForwardedToolCalls.Should().ContainSingle().Which;
        call.Status.Should().Be(LlmSessionForwardedToolCallStatus.Expired);
        // The "tool_call_expired" envelope is synthesized by the query reader at
        // the read boundary, not by the actor.
        call.Result.Should().BeNull();
        call.ReceivedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleReceiveForwardedToolResultAsync_ShouldRejectExpiredCall_AndPreserveExpiredState()
    {
        var actor = CreateActor("resp_1");
        var record = BuildRecord("resp_1");
        record.CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-2));
        record.Ttl = Duration.FromTimeSpan(TimeSpan.FromHours(1));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = record,
        });
        await actor.HandleRecordForwardedToolCallAsync(new RecordForwardedToolCallRequested
        {
            ResponseId = "resp_1",
            Call = BuildToolCall("call_1"),
        });
        await actor.HandleExpireResponseSessionAsync(new ExpireResponseSessionRequested
        {
            ResponseId = "resp_1",
            ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
        var versionAfterExpire = actor.State.LastAppliedEventVersion;

        var receive = () => actor.HandleReceiveForwardedToolResultAsync(new ReceiveForwardedToolResultRequested
        {
            ResponseId = "resp_1",
            CallId = "call_1",
            SchemaHash = "schema-1",
            Result = ResponsesJsonValues.ParseBoundaryPayload("""{"temperature":28}"""),
        });
        var resolve = () => actor.HandleResolveForwardedToolResultAsync(new ResolveForwardedToolResultRequested
        {
            ResponseId = "resp_1",
            CallId = "call_1",
        });

        await receive.Should().ThrowAsync<InvalidOperationException>();
        await resolve.Should().ThrowAsync<InvalidOperationException>();

        actor.State.LastAppliedEventVersion.Should().Be(versionAfterExpire);
        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Expired);
        actor.State.ForwardedToolCalls.Should().ContainSingle();
        var call = actor.State.ForwardedToolCalls[0];
        call.Status.Should().Be(LlmSessionForwardedToolCallStatus.Expired);
        call.Result.Should().BeNull();
        call.ResolvedAt.Should().BeNull();
    }

    private static LlmSessionGAgent CreateActor(string responseId) =>
        GAgentServiceTestKit.CreateStatefulAgent<LlmSessionGAgent, LlmSessionState>(
            new InMemoryEventStore(),
            "response-session-actor-" + responseId,
            static () => new LlmSessionGAgent());

    private static LlmSessionRecord BuildRecord(string responseId) =>
        new()
        {
            ResponseId = responseId,
            ScopeId = "user-1",
            OwnerSubject = "user-1",
            OriginKind = LlmSessionOriginKind.ApiKey,
            PreviousResponseId = string.Empty,
            Status = LlmSessionStatus.Unspecified,
            CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            Ttl = Duration.FromTimeSpan(TimeSpan.FromHours(24)),
        };

    private static LlmSessionForwardedToolCall BuildToolCall(string callId) =>
        new()
        {
            CallId = callId,
            ToolName = "get_weather",
            SchemaHash = "schema-1",
            Arguments = ResponsesJsonValues.ParseBoundaryPayload("""{"city":"Singapore"}"""),
            Status = LlmSessionForwardedToolCallStatus.Pending,
            EmittedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            Expiry = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(1)),
        };

    private static LlmSessionCompletion BuildCompletion(string text) =>
        new()
        {
            OutputText = text,
            CompletedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            ToolCalls =
            {
                new LlmSessionCompletedToolCall
                {
                    CallId = "call_done",
                    ToolName = "get_weather",
                    Result = ResponsesJsonValues.ParseBoundaryPayload("""{"ok":true}"""),
                },
            },
        };

    private static DateTimeOffset ResolveExpiry(LlmSessionRecord record) =>
        (record.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow)
        .Add(record.Ttl?.ToTimeSpan() ?? TimeSpan.FromHours(24));
}
