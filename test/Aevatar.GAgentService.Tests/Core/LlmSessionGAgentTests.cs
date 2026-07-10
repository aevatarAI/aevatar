using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task HandleUpdateStatusAsync_ShouldIgnoreUpdate_WhenAlreadyTerminal()
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });
        await actor.HandleUpdateStatusAsync(new UpdateResponseSessionStatusRequested
        {
            ResponseId = "resp_1",
            Status = LlmSessionStatus.Completed,
        });
        var versionAfterCompletion = actor.State.LastAppliedEventVersion;

        // A late Failed update (e.g. a streaming-observation timeout marking Failed after the run
        // already Completed on a long turn) must be an idempotent no-op, not throw — otherwise the
        // actor burns its runtime retry budget and logs "is Completed and cannot transition".
        var act = () => actor.HandleUpdateStatusAsync(new UpdateResponseSessionStatusRequested
        {
            ResponseId = "resp_1",
            Status = LlmSessionStatus.Failed,
        });

        await act.Should().NotThrowAsync();
        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Completed);
        actor.State.LastAppliedEventVersion.Should().Be(versionAfterCompletion);
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
    public void RuntimeToolArgumentsValue_WithLegacyJsonOnly_ShouldParseArguments()
    {
        var method = typeof(LlmSessionGAgent).GetMethod(
            "RuntimeToolArgumentsValue",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = (Google.Protobuf.WellKnownTypes.Value)method!.Invoke(null,
        [
            new LlmSessionRuntimeToolCall
            {
                CallId = "call_legacy",
                ToolName = "get_weather",
                ArgumentsJson = """{"city":"Paris"}""",
            },
        ])!;

        result.StructValue.Fields["city"].StringValue.Should().Be("Paris");
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
        actor.State.Completion.ToolCalls.Should().BeEmpty();
        actor.State.LastAppliedEventVersion.Should().Be(2);
    }

    [Fact]
    public async Task HandleRecordCompletionAsync_ShouldKeepOnlyPreviouslyForwardedToolCalls()
    {
        var actor = CreateActor("resp_1");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });
        await actor.HandleRecordForwardedToolCallAsync(new RecordForwardedToolCallRequested
        {
            ResponseId = "resp_1",
            Call = BuildToolCall("call_done"),
        });

        var completion = BuildCompletion("done");
        completion.ToolCalls.Add(new LlmSessionCompletedToolCall
        {
            CallId = "call_owned",
            ToolName = "aevatar_invoke_team",
            Result = ResponsesJsonValues.ParseBoundaryPayload("""{"team_id":"team-1"}"""),
        });

        await actor.HandleRecordCompletionAsync(new RecordResponseSessionCompletionRequested
        {
            ResponseId = "resp_1",
            Completion = completion,
        });

        actor.State.Completion!.ToolCalls.Should().ContainSingle()
            .Which.CallId.Should().Be("call_done");
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

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldCompleteFromStreamingText()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk { DeltaContent = "hello " },
                new LLMStreamChunk
                {
                    DeltaContent = "world",
                    Usage = new TokenUsage(3, 2, 5),
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActor("resp_1", services => services.AddSingleton<ILLMProviderFactory>(provider));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });

        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_1"));

        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Completed);
        actor.State.ActiveRun.Should().NotBeNull();
        actor.State.ActiveRun!.RunId.Should().Be("run_1");
        actor.State.ActiveRun.OutputText.Should().Be("hello world");
        actor.State.ActiveRun.Usage!.TotalTokens.Should().Be(5);
        actor.State.Completion!.OutputText.Should().Be("hello world");
        actor.State.Completion.Usage!.PromptTokens.Should().Be(3);
        provider.Requests.Should().ContainSingle()
            .Which.CallerContext!.ResponseId.Should().Be("resp_1");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldPersistTypedRunStartedEventBeforeOutput()
    {
        var eventStore = new InMemoryEventStore();
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaContent = "done",
                    IsLast = true,
                },
            ],
        ]);
        var requestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:00+00:00"));
        var actor = CreateActorWithStore(
            "resp_started",
            eventStore,
            services => services.AddSingleton<ILLMProviderFactory>(provider));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_started"),
        });

        var request = BuildRunRequest("resp_started");
        request.RequestedAt = requestedAt;
        await DispatchRunRequestedAsync(actor, request);

        var runEvents = (await eventStore.GetEventsAsync(actor.Id))
            .Select(static evt => evt.EventData)
            .Where(static payload =>
                payload.Is(LlmRunStartedEvent.Descriptor) ||
                payload.Is(LlmStreamChunkObserved.Descriptor) ||
                payload.Is(LlmRunCompleted.Descriptor))
            .ToArray();
        runEvents.Should().HaveCount(3);
        runEvents[0].Is(LlmRunStartedEvent.Descriptor).Should().BeTrue();
        var started = runEvents[0].Unpack<LlmRunStartedEvent>();
        started.ResponseId.Should().Be("resp_started");
        started.RunId.Should().Be("run_1");
        started.Sequence.Should().Be(1);
        started.StartedAt.Should().Be(requestedAt);
        runEvents[1].Unpack<LlmStreamChunkObserved>().Sequence.Should().Be(2);
        runEvents[2].Unpack<LlmRunCompleted>().Sequence.Should().Be(3);

        actor.State.ActiveRun.Should().NotBeNull();
        actor.State.ActiveRun!.StartedAt.Should().Be(requestedAt);
        actor.State.ActiveRun.LastAppliedSequence.Should().Be(3);
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldIgnoreDuplicateRunningRunAdmission()
    {
        var eventStore = new InMemoryEventStore();
        await eventStore.AppendAsync(
            "response-session-actor-resp_duplicate_run",
            [
                StateEvent(1, new LlmSessionRegisteredEvent { Record = BuildRecord("resp_duplicate_run") }),
                StateEvent(2, new LlmRunStartedEvent
                {
                    ResponseId = "resp_duplicate_run",
                    RunId = "run_1",
                    Sequence = 1,
                    StartedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:00+00:00")),
                }),
            ],
            expectedVersion: 0);
        var actor = CreateActorWithStore(
            "resp_duplicate_run",
            eventStore,
            services => services.AddSingleton<ILLMProviderFactory, ThrowingLlmProviderFactory>());
        await actor.ActivateAsync();
        var versionAfterActivation = actor.State.LastAppliedEventVersion;

        await actor.HandleLlmRunRequestedAsync(new LlmRunRequested
        {
            ResponseId = "resp_duplicate_run",
            RunId = "run_1",
        });

        actor.State.LastAppliedEventVersion.Should().Be(versionAfterActivation);
    }

    [Fact]
    public async Task HandleCancelLlmRunRequestedAsync_ShouldRecordActorOwnedCancellation()
    {
        var eventStore = new InMemoryEventStore();
        var actor = CreateActorWithStore("resp_cancel", eventStore);
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_cancel"),
        });

        await actor.HandleCancelLlmRunRequestedAsync(new CancelLlmRunRequested
        {
            ResponseId = "resp_cancel",
            RunId = "resp_cancel:llm-run",
            CancelledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:00+00:00")),
        });

        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Cancelled);
        actor.State.ActiveRun!.RunId.Should().Be("resp_cancel:llm-run");
        actor.State.ActiveRun.Status.Should().Be(4);
        var cancelled = (await eventStore.GetEventsAsync(actor.Id))
            .Select(static evt => evt.EventData)
            .Where(static payload => payload.Is(LlmRunCancelled.Descriptor))
            .Select(static payload => payload.Unpack<LlmRunCancelled>())
            .Should()
            .ContainSingle()
            .Subject;
        cancelled.ResponseId.Should().Be("resp_cancel");
        cancelled.RunId.Should().Be("resp_cancel:llm-run");
    }

    [Fact]
    public async Task HandleRecordRunStartedAsync_ShouldPersistReadyFact_AndDispatchTransientExecutionCommand()
    {
        var eventStore = new InMemoryEventStore();
        var observations = new List<string>();
        var executor = new RecordingLlmRunExecutor(observations);
        var actor = CreateActorWithStore(
            "resp_off_actor_started",
            eventStore,
            services => services.AddSingleton<ILlmRunExecutor>(executor));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_off_actor_started"),
        });

        var requestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:00+00:00"));
        var request = BuildRunRequest("resp_off_actor_started");
        request.RequestedAt = requestedAt;

        await actor.HandleRecordRunStartedAsync(new RecordLlmRunStarted
        {
            Command = request,
            StartedAt = requestedAt,
        });

        var runEvents = (await eventStore.GetEventsAsync(actor.Id))
            .Select(static evt => evt.EventData)
            .Where(static payload =>
                payload.Is(LlmRunStartedEvent.Descriptor) ||
                payload.Is(LlmRunExecutionReadyEvent.Descriptor))
            .ToArray();
        runEvents.Should().HaveCount(2);
        var started = runEvents[0].Unpack<LlmRunStartedEvent>();
        started.ResponseId.Should().Be("resp_off_actor_started");
        started.RunId.Should().Be("run_1");
        started.StartedAt.Should().Be(requestedAt);
        var ready = runEvents[1].Unpack<LlmRunExecutionReadyEvent>();
        ready.ResponseId.Should().Be("resp_off_actor_started");
        ready.RunId.Should().Be("run_1");
        System.Text.Encoding.UTF8.GetString(runEvents[1].ToByteArray())
            .Should()
            .NotContain(request.BearerToken);
        actor.State.ActiveRun.Should().NotBeNull();
        actor.State.ActiveRun!.StartedAt.Should().Be(requestedAt);
        var executionRequest = executor.ExecuteRequests.Should().ContainSingle().Subject;
        executionRequest.ResponseId.Should().Be("resp_off_actor_started");
        executionRequest.RunId.Should().Be("run_1");
        executionRequest.Command.Should().NotBeSameAs(request);
        executionRequest.Command.Messages.Should().BeEquivalentTo(request.Messages);
        executionRequest.Command.BearerToken.Should().Be(request.BearerToken);
        executionRequest.Command.ToolContext.Should().BeEquivalentTo(request.ToolContext);
        observations.Should().ContainSingle("executor:execute");
    }

    [Fact]
    public async Task RecordCommands_ShouldAssignActorOwnedSequence_AndIgnoreDuplicateRecordId()
    {
        var actor = CreateActor("resp_records", services => services.AddSingleton<ILlmRunExecutor, NoOpLlmRunExecutor>());
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_records"),
        });
        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_records"));

        await actor.HandleRecordStreamChunkObservedAsync(new RecordLlmStreamChunkObserved
        {
            ResponseId = "resp_records",
            RunId = "run_1",
            RecordId = "run_1:manual:1",
            Round = 0,
            DeltaText = " late",
        });
        var versionAfterRecord = actor.State.LastAppliedEventVersion;

        await actor.HandleRecordStreamChunkObservedAsync(new RecordLlmStreamChunkObserved
        {
            ResponseId = "resp_records",
            RunId = "run_1",
            RecordId = "run_1:manual:1",
            Round = 0,
            DeltaText = " duplicate",
        });

        actor.State.LastAppliedEventVersion.Should().Be(versionAfterRecord);
        actor.State.ActiveRun!.AppliedRecordIds.Should().Contain("run_1:manual:1");
        actor.State.ActiveRun.OutputText.Should().Be(" late");
        actor.State.ActiveRun.LastAppliedSequence.Should().Be(2);
    }

    [Fact]
    public async Task RecordCommands_ShouldRejectOutOfRunRecord()
    {
        var actor = CreateActor("resp_records", services => services.AddSingleton<ILlmRunExecutor, NoOpLlmRunExecutor>());
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_records"),
        });
        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_records"));

        var act = () => actor.HandleRecordStreamChunkObservedAsync(new RecordLlmStreamChunkObserved
        {
            ResponseId = "resp_records",
            RunId = "foreign-run",
            RecordId = "foreign-run:chunk:1",
            DeltaText = "wrong",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*active run is 'run_1' and cannot record run 'foreign-run'*");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldScheduleRunTimeoutBeforeStartingExecutor()
    {
        var observations = new List<string>();
        var scheduler = new RecordingRuntimeCallbackScheduler(observations);
        var executor = new RecordingLlmRunExecutor(observations);
        var requestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:00+00:00"));
        var actor = CreateActor(
            "resp_timeout_schedule",
            services =>
            {
                services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler);
                services.AddSingleton<ILlmRunExecutor>(executor);
            });
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_timeout_schedule"),
        });

        var request = BuildRunRequest("resp_timeout_schedule");
        request.RequestedAt = requestedAt;
        request.TimeoutAfter = Duration.FromTimeSpan(TimeSpan.FromMinutes(5));

        await DispatchRunRequestedAsync(actor, request);

        var runTimeout = scheduler.TimeoutRequests.Should().ContainSingle(request =>
            string.Equals(
                request.CallbackId,
                "llm-run-timeout:resp_timeout_schedule:run_1",
                StringComparison.Ordinal)).Subject;
        runTimeout.ActorId.Should().Be(actor.Id);
        runTimeout.DueTime.Should().BeGreaterThan(TimeSpan.Zero);
        var payload = runTimeout.TriggerEnvelope.Payload.Unpack<FinalizeLlmRunTimedOut>();
        payload.ResponseId.Should().Be("resp_timeout_schedule");
        payload.RunId.Should().Be("run_1");
        payload.RecordId.Should().Be("run_1:timeout");
        payload.TimedOutAt.Should().Be(
            Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:05:00+00:00")));
        executor.ExecuteRequests.Should().ContainSingle()
            .Which.RunId.Should().Be("run_1");
        observations.IndexOf("schedule:llm-run-timeout:resp_timeout_schedule:run_1")
            .Should().BeLessThan(observations.IndexOf("executor:execute"));
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_WithoutTimeoutAfter_ShouldScheduleRunTimeoutInMinutes_NotSessionTtl()
    {
        // Crash/abandon finalizer regression guard: when a run carries no explicit
        // TimeoutAfter, the scheduled run timeout must fall back to the run-execution
        // timeout (minutes), NOT the 24h session TTL. Otherwise an abandoned/crashed
        // off-grain run would only be finalized ~24h later.
        var scheduler = new RecordingRuntimeCallbackScheduler(new List<string>());
        var executor = new RecordingLlmRunExecutor();
        var requestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:00+00:00"));
        var actor = CreateActor(
            "resp_timeout_default",
            services =>
            {
                services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler);
                services.AddSingleton<ILlmRunExecutor>(executor);
            });
        // BuildRecord pins a 24h session TTL; the run timeout must not inherit it.
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_timeout_default"),
        });

        var request = BuildRunRequest("resp_timeout_default");
        request.RequestedAt = requestedAt;
        // Intentionally no request.TimeoutAfter.

        await DispatchRunRequestedAsync(actor, request);

        var runTimeout = scheduler.TimeoutRequests.Should().ContainSingle(timeout =>
            string.Equals(
                timeout.CallbackId,
                "llm-run-timeout:resp_timeout_default:run_1",
                StringComparison.Ordinal)).Subject;
        var payload = runTimeout.TriggerEnvelope.Payload.Unpack<FinalizeLlmRunTimedOut>();
        payload.TimedOutAt.Should().Be(
            Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:10:00+00:00")));
        (payload.TimedOutAt.ToDateTimeOffset() - requestedAt.ToDateTimeOffset())
            .Should().BeLessThan(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_WhenRunTimeoutSchedulingFails_ShouldFailRunWithoutStartingExecutor()
    {
        var scheduler = new RunTimeoutFailingRuntimeCallbackScheduler();
        var executor = new RecordingLlmRunExecutor();
        var actor = CreateActor(
            "resp_timeout_schedule_failure",
            services =>
            {
                services.AddSingleton<IActorRuntimeCallbackScheduler>(scheduler);
                services.AddSingleton<ILlmRunExecutor>(executor);
            });
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_timeout_schedule_failure"),
        });

        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_timeout_schedule_failure"));

        scheduler.TimeoutRequests.Should().ContainSingle(request =>
            string.Equals(
                request.CallbackId,
                "llm-run-timeout:resp_timeout_schedule_failure:run_1",
                StringComparison.Ordinal));
        executor.ExecuteRequests.Should().BeEmpty();
        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Failed);
        actor.State.ActiveRun.Should().NotBeNull();
        actor.State.ActiveRun!.Status.Should().Be(3);
        actor.State.ActiveRun.FailureCode.Should().Be("run_timeout_schedule_failed");
        actor.State.ActiveRun.FailureMessage.Should().Be("synthetic scheduler failure.");
        actor.State.ActiveRun.AppliedRecordIds.Should().Contain("run_1:timeout-schedule-failed");
        actor.State.ActiveRun.LastAppliedSequence.Should().Be(2);
        actor.State.Completion.Should().NotBeNull();
        actor.State.Completion!.FailureCode.Should().Be("run_timeout_schedule_failed");
        actor.State.Completion.FailureMessage.Should().Be("synthetic scheduler failure.");
    }

    [Fact]
    public async Task FinalizeLlmRunTimedOutAsync_ShouldCommitRunTimeoutOnlyForActiveRun()
    {
        var actor = CreateActor("resp_timeout", services => services.AddSingleton<ILlmRunExecutor, NoOpLlmRunExecutor>());
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_timeout"),
        });
        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_timeout"));

        await actor.HandleFinalizeLlmRunTimedOutAsync(new FinalizeLlmRunTimedOut
        {
            ResponseId = "resp_timeout",
            RunId = "run_1",
            RecordId = "run_1:timeout",
            TimedOutAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:01:00+00:00")),
        });
        var versionAfterTimeout = actor.State.LastAppliedEventVersion;

        await actor.HandleFinalizeLlmRunTimedOutAsync(new FinalizeLlmRunTimedOut
        {
            ResponseId = "resp_timeout",
            RunId = "run_1",
            RecordId = "run_1:timeout",
            TimedOutAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:02:00+00:00")),
        });
        await actor.HandleFinalizeLlmRunTimedOutAsync(new FinalizeLlmRunTimedOut
        {
            ResponseId = "resp_timeout",
            RunId = "stale-run",
            RecordId = "stale-run:timeout",
            TimedOutAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:03:00+00:00")),
        });

        actor.State.LastAppliedEventVersion.Should().Be(versionAfterTimeout);
        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Failed);
        actor.State.ActiveRun!.Status.Should().Be(3);
        actor.State.ActiveRun.FailureCode.Should().Be("run_timeout");
        actor.State.ActiveRun.AppliedRecordIds.Should().Contain("run_1:timeout");
        actor.State.ActiveRun.LastAppliedSequence.Should().Be(2);
        actor.State.Completion!.FailureCode.Should().Be("run_timeout");
        actor.State.Completion.FailureMessage.Should().Be("LLM run timed out.");
    }

    [Fact]
    public async Task ActivateAsync_ShouldReplayOnlyConsecutiveRunEventSequences()
    {
        var eventStore = new InMemoryEventStore();
        var actorId = "response-session-actor-resp_sequence";
        await eventStore.AppendAsync(
            actorId,
            [
                StateEvent(1, new LlmSessionRegisteredEvent { Record = BuildRecord("resp_sequence") }),
                StateEvent(2, new LlmRunStartedEvent
                {
                    ResponseId = "resp_sequence",
                    RunId = "run_1",
                    Sequence = 1,
                    StartedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:00+00:00")),
                }),
                StateEvent(3, new LlmStreamChunkObserved
                {
                    ResponseId = "resp_sequence",
                    RunId = "run_1",
                    Sequence = 2,
                    DeltaText = "first",
                    ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:01+00:00")),
                }),
                StateEvent(4, new LlmStreamChunkObserved
                {
                    ResponseId = "resp_sequence",
                    RunId = "run_1",
                    Sequence = 2,
                    DeltaText = "duplicate",
                    ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:02+00:00")),
                }),
                StateEvent(5, new LlmStreamChunkObserved
                {
                    ResponseId = "resp_sequence",
                    RunId = "run_1",
                    Sequence = 4,
                    DeltaText = "out-of-order",
                    ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:03+00:00")),
                }),
                StateEvent(6, new LlmRunCompleted
                {
                    ResponseId = "resp_sequence",
                    RunId = "run_1",
                    Sequence = 3,
                    OutputText = "first",
                    CompletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:04+00:00")),
                }),
                StateEvent(7, new LlmStreamChunkObserved
                {
                    ResponseId = "resp_sequence",
                    RunId = "run_1",
                    Sequence = 4,
                    DeltaText = "late",
                    ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:05+00:00")),
                }),
            ],
            expectedVersion: 0);
        var actor = CreateActorWithStore("resp_sequence", eventStore);

        await actor.ActivateAsync();

        actor.State.LastAppliedEventVersion.Should().Be(4);
        actor.State.ActiveRun.Should().NotBeNull();
        actor.State.ActiveRun!.OutputText.Should().Be("first");
        actor.State.ActiveRun.Status.Should().Be(2);
        actor.State.ActiveRun.LastAppliedSequence.Should().Be(3);
        actor.State.Completion!.OutputText.Should().Be("first");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldRecordForwardedToolCallCompletion()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":""",
                    },
                },
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = "\"Singapore\"}",
                    },
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActor("resp_1", services => services.AddSingleton<ILLMProviderFactory>(provider));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });

        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_1", BuildForwardedSelection()));

        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Completed);
        actor.State.ForwardedToolCalls.Should().ContainSingle();
        var forwarded = actor.State.ForwardedToolCalls[0];
        forwarded.CallId.Should().Be("call_1");
        forwarded.ToolName.Should().Be("get_weather");
        forwarded.SchemaHash.Should().Be("schema-1");
        forwarded.Status.Should().Be(LlmSessionForwardedToolCallStatus.Pending);
        ResponsesJsonValues.ToBoundaryJson(forwarded.Arguments).Should().Be("""{"city":"Singapore"}""");
        actor.State.ActiveRun!.ObservedToolCalls.Should().ContainSingle()
            .Which.Arguments.Fields["city"].StringValue.Should().Be("Singapore");
        actor.State.Completion!.ToolCalls.Should().ContainSingle()
            .Which.ToolName.Should().Be("get_weather");
        ResponsesJsonValues.ToBoundaryJson(actor.State.Completion.ToolCalls[0].Result)
            .Should().Be("""{"city":"Singapore"}""");
    }

    [Fact]
    public async Task HandleLlmSessionForwardedToolCallEmittedAsync_ShouldPersistForwardedToolCallAndIgnoreDuplicate()
    {
        var actor = CreateActor("resp_forwarded_recorder");
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_forwarded_recorder"),
        });
        var emitted = new LlmSessionForwardedToolCallEmittedEvent
        {
            ResponseId = "resp_forwarded_recorder",
            Call = BuildToolCall("call_1"),
        };

        await actor.HandleLlmSessionForwardedToolCallEmittedAsync(emitted);
        var versionAfterFirstEmission = actor.State.LastAppliedEventVersion;
        await actor.HandleLlmSessionForwardedToolCallEmittedAsync(emitted.Clone());

        actor.State.LastAppliedEventVersion.Should().Be(versionAfterFirstEmission);
        actor.State.ForwardedToolCalls.Should().ContainSingle();
        var forwarded = actor.State.ForwardedToolCalls[0];
        forwarded.CallId.Should().Be("call_1");
        forwarded.ToolName.Should().Be("get_weather");
        forwarded.SchemaHash.Should().Be("schema-1");
        forwarded.Status.Should().Be(LlmSessionForwardedToolCallStatus.Pending);
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldPreferTypedRuntimeMessageToolArguments()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaContent = "done",
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActor("resp_typed_runtime_message", services => services.AddSingleton<ILLMProviderFactory>(provider));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_typed_runtime_message"),
        });
        var request = BuildRunRequest("resp_typed_runtime_message");
        request.Messages.Clear();
        request.Messages.Add(new LlmSessionRuntimeChatMessage
        {
            Role = "assistant",
            ToolCalls =
            {
                new LlmSessionRuntimeToolCall
                {
                    CallId = "call_typed_args",
                    ToolName = "get_weather",
                    ArgumentsJson = "not json {",
                    Arguments = new Struct
                    {
                        Fields =
                        {
                            ["city"] = Google.Protobuf.WellKnownTypes.Value.ForString("Singapore"),
                        },
                    },
                },
            },
        });

        await DispatchRunRequestedAsync(actor, request);

        var requestMessage = provider.Requests.Should().ContainSingle().Subject.Messages
            .Single(message => message.ToolCalls != null && message.ToolCalls.Count == 1);
        var toolCall = requestMessage.ToolCalls![0];
        toolCall.ArgumentsJson.Should().Contain("Singapore");
        toolCall.ArgumentsJson.Should().NotBe("not json {");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldUseLegacyRuntimeMessageToolArguments_WhenTypedArgumentsAreEmpty()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaContent = "done",
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActor("resp_legacy_runtime_message", services => services.AddSingleton<ILLMProviderFactory>(provider));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_legacy_runtime_message"),
        });
        var request = BuildRunRequest("resp_legacy_runtime_message");
        request.Messages.Clear();
        request.Messages.Add(new LlmSessionRuntimeChatMessage
        {
            Role = "assistant",
            ToolCalls =
            {
                new LlmSessionRuntimeToolCall
                {
                    CallId = "call_legacy_args",
                    ToolName = "get_weather",
                    ArgumentsJson = """{"city":"Paris"}""",
                },
            },
        });

        await DispatchRunRequestedAsync(actor, request);

        var toolCall = provider.Requests.Should().ContainSingle().Subject.Messages
            .Single(message => message.ToolCalls != null && message.ToolCalls.Count == 1)
            .ToolCalls![0];
        toolCall.ArgumentsJson.Should().Be("""{"city":"Paris"}""");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldUseTypedToolDeclarationParametersBeforeLegacyJson()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaContent = "done",
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActor("resp_typed_parameters", services => services.AddSingleton<ILLMProviderFactory>(provider));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_typed_parameters"),
        });
        var selection = BuildForwardedSelection();
        selection.ForwardedTools[0].ParametersJson = "not json {";
        selection.ForwardedTools[0].Parameters = new Struct
        {
            Fields =
            {
                ["type"] = Google.Protobuf.WellKnownTypes.Value.ForString("object"),
                ["typed"] = Google.Protobuf.WellKnownTypes.Value.ForBool(true),
            },
        };

        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_typed_parameters", selection));

        provider.Requests.Should().ContainSingle().Subject.Tools
            .Should()
            .ContainSingle(tool => tool.Name == "get_weather")
            .Which.ParametersSchema.Should().Contain("\"typed\": true");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldUseLegacyToolDeclarationParameters_WhenTypedParametersAreEmpty()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaContent = "done",
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActor("resp_legacy_parameters", services => services.AddSingleton<ILLMProviderFactory>(provider));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_legacy_parameters"),
        });
        var selection = BuildForwardedSelection();
        selection.ForwardedTools[0].Parameters.Fields.Clear();
        selection.ForwardedTools[0].ParametersJson = """{"type":"object","legacy":true}""";

        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_legacy_parameters", selection));

        provider.Requests.Should().ContainSingle().Subject.Tools
            .Should()
            .ContainSingle(tool => tool.Name == "get_weather")
            .Which.ParametersSchema.Should().Be("""{"type":"object","legacy":true}""");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldUseTypedToolChoiceHintArgumentsBeforeLegacyJson()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":"Singapore"}""",
                    },
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActor("resp_typed_hint", services => services.AddSingleton<ILLMProviderFactory>(provider));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_typed_hint"),
        });
        var selection = BuildForwardedSelection();
        selection.ToolChoiceHintName = "get_weather";
        selection.ToolChoiceHintArgumentsJson = "not json {";
        selection.ToolChoiceHintArguments = new Struct
        {
            Fields =
            {
                ["actor_id"] = Google.Protobuf.WellKnownTypes.Value.ForString("actor-from-typed-hint"),
            },
        };

        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_typed_hint", selection));

        var forwarded = actor.State.ForwardedToolCalls.Should().ContainSingle().Subject;
        forwarded.Arguments.StructValue.Fields["actor_id"].StringValue.Should().Be("actor-from-typed-hint");
        forwarded.Arguments.StructValue.Fields["city"].StringValue.Should().Be("Singapore");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldUseLegacyToolChoiceHintArguments_WhenTypedArgumentsAreEmpty()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":"Singapore"}""",
                    },
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActor("resp_legacy_hint", services => services.AddSingleton<ILLMProviderFactory>(provider));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_legacy_hint"),
        });
        var selection = BuildForwardedSelection();
        selection.ToolChoiceHintName = "get_weather";
        selection.ToolChoiceHintArgumentsJson = """{"actor_id":"actor-from-legacy-hint"}""";

        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_legacy_hint", selection));

        var forwarded = actor.State.ForwardedToolCalls.Should().ContainSingle().Subject;
        forwarded.Arguments.StructValue.Fields["actor_id"].StringValue.Should().Be("actor-from-legacy-hint");
        forwarded.Arguments.StructValue.Fields["city"].StringValue.Should().Be("Singapore");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldMergeTypedToolCallDeltasForSameCall()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = "{\"city\":\"Singapore\",",
                    },
                },
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = "\"unit\":\"celsius\"}",
                    },
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActor("resp_merge_tool_deltas", services => services.AddSingleton<ILLMProviderFactory>(provider));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_merge_tool_deltas"),
        });

        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_merge_tool_deltas", BuildForwardedSelection()));

        var forwarded = actor.State.ForwardedToolCalls.Should().ContainSingle().Subject;
        forwarded.Arguments.StructValue.Fields["city"].StringValue.Should().Be("Singapore");
        forwarded.Arguments.StructValue.Fields["unit"].StringValue.Should().Be("celsius");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldExecuteSubstitutedToolAndContinueNextRound()
    {
        var tool = new RecordingAgentTool("get_weather", """{"temperature":28}""");
        var toolProvider = new StaticResponsesToolProvider(substituteTools: [tool]);
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":"Singapore"}""",
                    },
                    IsLast = true,
                },
            ],
            [
                new LLMStreamChunk
                {
                    DeltaContent = "local result accepted",
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActor(
            "resp_1",
            services =>
            {
                services.AddSingleton<ILLMProviderFactory>(provider);
                services.AddSingleton<IResponsesToolProvider>(toolProvider);
            });
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });

        var selection = BuildForwardedSelection();
        selection.SubstitutedToolNames.Add("get_weather");
        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_1", selection));

        tool.Executions.Should().ContainSingle().Which.Should().Be("""{"city":"Singapore"}""");
        provider.Requests.Should().HaveCount(2);
        provider.Requests[1].Messages.Should().Contain(message =>
            string.Equals(message.Role, "tool", StringComparison.Ordinal) &&
            string.Equals(message.Content, """{"temperature":28}""", StringComparison.Ordinal));
        actor.State.ForwardedToolCalls.Should().BeEmpty();
        actor.State.ActiveRun!.Status.Should().Be(2);
        actor.State.Completion!.OutputText.Should().Be("local result accepted");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldPersistTypedLocalToolResultPayload()
    {
        var eventStore = new InMemoryEventStore();
        var tool = new RecordingAgentTool("get_weather", """{"temperature":28}""");
        var toolProvider = new StaticResponsesToolProvider(substituteTools: [tool]);
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":"Singapore"}""",
                    },
                    IsLast = true,
                },
            ],
            [
                new LLMStreamChunk
                {
                    DeltaContent = "local result accepted",
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActorWithStore(
            "resp_typed_local_result",
            eventStore,
            services =>
            {
                services.AddSingleton<ILLMProviderFactory>(provider);
                services.AddSingleton<IResponsesToolProvider>(toolProvider);
            });
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_typed_local_result"),
        });
        var selection = BuildForwardedSelection();
        selection.SubstitutedToolNames.Add("get_weather");

        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_typed_local_result", selection));

        var localObserved = (await eventStore.GetEventsAsync(actor.Id))
            .Select(static evt => evt.EventData)
            .Where(static payload => payload.Is(LlmToolCallObserved.Descriptor))
            .Select(static payload => payload.Unpack<LlmToolCallObserved>())
            .Should()
            .ContainSingle(observed => !observed.Forwarded)
            .Subject;
        localObserved.LocalResultJson.Should().Be("""{"temperature":28}""");
        localObserved.LocalResult.StructValue.Fields["temperature"].NumberValue.Should().Be(28);
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldRecordForwardedToolCallAtomicallyWithCompletion()
    {
        var eventStore = new InMemoryEventStore();
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":"Singapore"}""",
                    },
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActorWithStore(
            "resp_forwarded_atomic",
            eventStore,
            services => services.AddSingleton<ILLMProviderFactory>(provider));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_forwarded_atomic"),
        });

        await actor.HandleLlmRunRequestedAsync(BuildRunRequest("resp_forwarded_atomic", BuildForwardedSelection()));

        var events = (await eventStore.GetEventsAsync(actor.Id))
            .Select(static evt => evt.EventData)
            .ToArray();
        events.Should().NotContain(payload => payload.Is(LlmSessionForwardedToolCallEmittedEvent.Descriptor));
        var completed = events
            .Where(static payload => payload.Is(LlmRunCompleted.Descriptor))
            .Select(static payload => payload.Unpack<LlmRunCompleted>())
            .Should()
            .ContainSingle()
            .Subject;
        completed.ForwardedToolCalls.Should().ContainSingle()
            .Which.CallId.Should().Be("call_1");
        completed.ForwardedToolCallRecords.Should().ContainSingle()
            .Which.SchemaHash.Should().Be("schema-1");
        actor.State.ForwardedToolCalls.Should().ContainSingle()
            .Which.Status.Should().Be(LlmSessionForwardedToolCallStatus.Pending);
        actor.State.Completion!.ToolCalls.Should().ContainSingle()
            .Which.CallId.Should().Be("call_1");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_WhenLocalToolThrows_ShouldRecordSafeToolOutputAndContinueNextRound()
    {
        var eventStore = new InMemoryEventStore();
        var tool = new ThrowingAgentTool(
            "get_weather",
            new InvalidOperationException("secret token /Users/me/path"));
        var toolProvider = new StaticResponsesToolProvider(substituteTools: [tool]);
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":"Singapore"}""",
                    },
                    IsLast = true,
                },
            ],
            [
                new LLMStreamChunk
                {
                    DeltaContent = "safe output accepted",
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActorWithStore(
            "resp_safe_tool_failure",
            eventStore,
            services =>
            {
                services.AddSingleton<ILLMProviderFactory>(provider);
                services.AddSingleton<IResponsesToolProvider>(toolProvider);
            });
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_safe_tool_failure"),
        });
        var selection = BuildForwardedSelection();
        selection.SubstitutedToolNames.Add("get_weather");

        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_safe_tool_failure", selection));

        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Completed);
        actor.State.Completion!.OutputText.Should().Be("safe output accepted");
        provider.Requests.Should().HaveCount(2);
        var toolMessage = provider.Requests[1].Messages.Single(message =>
            string.Equals(message.Role, "tool", StringComparison.Ordinal));
        toolMessage.Content.Should().Contain("aevatar_local_tool_execution_failed");
        toolMessage.Content.Should().Contain("get_weather");
        toolMessage.Content.Should().NotContain("secret token");
        toolMessage.Content.Should().NotContain("/Users/me/path");

        var localObserved = (await eventStore.GetEventsAsync(actor.Id))
            .Select(static evt => evt.EventData)
            .Where(static payload => payload.Is(LlmToolCallObserved.Descriptor))
            .Select(static payload => payload.Unpack<LlmToolCallObserved>())
            .Should()
            .ContainSingle(observed => !observed.Forwarded)
            .Subject;
        localObserved.LocalResultJson.Should().Be(toolMessage.Content);
        localObserved.LocalResult.StructValue.Fields["error"]
            .StructValue.Fields["code"].StringValue.Should().Be("aevatar_local_tool_execution_failed");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldPropagateTypedToolContextAcrossProviderAndToolScopes()
    {
        var tool = new RecordingAgentTool("get_weather", """{"temperature":28}""");
        var toolProvider = new StaticResponsesToolProvider(substituteTools: [tool]);
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":"Singapore"}""",
                    },
                    IsLast = true,
                },
            ],
            [
                new LLMStreamChunk
                {
                    DeltaContent = "done",
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActor(
            "resp_1",
            services =>
            {
                services.AddSingleton<ILLMProviderFactory>(provider);
                services.AddSingleton<IResponsesToolProvider>(toolProvider);
            });
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });

        var selection = BuildForwardedSelection();
        selection.SubstitutedToolNames.Add("get_weather");
        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_1", selection));

        provider.Requests.Should().HaveCount(2);
        foreach (var request in provider.Requests)
        {
            request.ToolContext.Should().NotBeNull();
            request.ToolContext!.Request.RequestId.Should().Be("resp_1");
            request.ToolContext.Request.CallId.Should().BeNull();
            request.ToolContext.Credentials.NyxIdAccessToken.Should().Be("token-1");
            request.ToolContext.Caller.ScopeId.Should().Be("user-1");
            request.ToolContext.Caller.OwnerSubject.Should().Be("user-1");
            request.ToolContext.Caller.ResponseId.Should().Be("resp_1");
            request.ToolContext.Routing.NyxIdRoutePreference.Should().BeNull();
        }

        provider.StreamContexts.Should().HaveCount(2);
        foreach (var context in provider.StreamContexts)
        {
            context.Should().NotBeNull();
            context!.Request.RequestId.Should().Be("resp_1");
            context.Request.CallId.Should().BeNull();
            context.Credentials.NyxIdAccessToken.Should().Be("token-1");
            context.Caller.ScopeId.Should().Be("user-1");
            context.Caller.OwnerSubject.Should().Be("user-1");
            context.Caller.ResponseId.Should().Be("resp_1");
        }

        toolProvider.SubstituteContexts.Should().ContainSingle();
        var requestToolContext = provider.Requests[0].ToolContext;
        requestToolContext.Should().NotBeNull();
        toolProvider.SubstituteContexts[0].ToolContext.Should().BeEquivalentTo(
            requestToolContext!,
            options => options.Excluding(context => context.Channel.Platform));
        toolProvider.SubstituteContexts[0].ToolContext.Channel.Platform.Should().Be("ApiKey");

        tool.Executions.Should().ContainSingle().Which.Should().Be("""{"city":"Singapore"}""");
        tool.ExecutionContexts.Should().ContainSingle();
        tool.ExecutionContexts[0].Should().NotBeNull();
        tool.ExecutionContexts[0].Should().BeEquivalentTo(
            requestToolContext!,
            options => options.Excluding(context => context.Request.CallId));
        tool.ExecutionContexts[0]!.Request.CallId.Should().Be("call_1");
        AgentToolRequestContext.Current.Should().BeNull();
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_WhenToolProviderDiscoveryFails_ShouldContinueWithOtherProviders()
    {
        var tool = new RecordingAgentTool("get_weather", """{"temperature":28}""");
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":"Singapore"}""",
                    },
                    IsLast = true,
                },
            ],
            [
                new LLMStreamChunk
                {
                    DeltaContent = "done",
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActor(
            "resp_provider_discovery_failure",
            services =>
            {
                services.AddSingleton<ILLMProviderFactory>(provider);
                services.AddSingleton<IResponsesToolProvider>(new FaultingResponsesToolProvider());
                services.AddSingleton<IResponsesToolProvider>(new StaticResponsesToolProvider(substituteTools: [tool]));
            });
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_provider_discovery_failure"),
        });
        var selection = BuildForwardedSelection();
        selection.SubstitutedToolNames.Add("get_weather");

        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_provider_discovery_failure", selection));

        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Completed);
        tool.Executions.Should().ContainSingle();
        actor.State.Completion!.OutputText.Should().Be("done");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldPreferCommandToolContextOverLegacyScalars()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaContent = "done",
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActor("resp_1", services => services.AddSingleton<ILLMProviderFactory>(provider));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });

        var request = BuildRunRequest("resp_1");
        request.ScopeId = "legacy-scope";
        request.OwnerSubject = "legacy-owner";
        request.BearerToken = "legacy-token";
        request.RoutePreference = "legacy-route";
        request.ToolContext = NewCommandToolContext(
            requestId: "typed-request",
            scopeId: "typed-scope",
            ownerSubject: "typed-owner",
            responseId: "typed-response",
            token: "typed-token",
            routePreference: "typed-route").ToPayload();

        await DispatchRunRequestedAsync(actor, request);

        var llmRequest = provider.Requests.Should().ContainSingle().Subject;
        llmRequest.ToolContext.Should().NotBeNull();
        llmRequest.ToolContext!.Request.RequestId.Should().Be("typed-request");
        llmRequest.CallerContext.Should().NotBeNull();
        llmRequest.CallerContext!.ScopeId.Should().Be("typed-scope");
        llmRequest.CallerContext.OwnerSubject.Should().Be("typed-owner");
        llmRequest.CallerContext.ResponseId.Should().Be("typed-response");
        llmRequest.CallerContext.Credentials.NyxIdBearer.Should().Be("typed-token");
        llmRequest.LlmControl.Should().NotBeNull();
        llmRequest.LlmControl!.NyxIdAccessToken.Should().Be("typed-token");
        llmRequest.LlmControl.NyxIdRoutePreference.Should().Be("typed-route");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldUseLegacyScalars_WhenCommandToolContextIsAbsent()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaContent = "done",
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActor("resp_1", services => services.AddSingleton<ILLMProviderFactory>(provider));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });

        var request = BuildRunRequest("resp_1");
        request.RoutePreference = "legacy-route";

        await DispatchRunRequestedAsync(actor, request);

        var llmRequest = provider.Requests.Should().ContainSingle().Subject;
        llmRequest.ToolContext.Should().NotBeNull();
        llmRequest.ToolContext!.Request.RequestId.Should().Be("resp_1");
        llmRequest.ToolContext.Caller.ScopeId.Should().Be("user-1");
        llmRequest.ToolContext.Caller.OwnerSubject.Should().Be("user-1");
        llmRequest.ToolContext.Credentials.NyxIdAccessToken.Should().Be("token-1");
        llmRequest.ToolContext.Routing.NyxIdRoutePreference.Should().Be("legacy-route");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_ShouldNotWriteOwnedControlKeysToProviderMetadata_WhenToolContextIsTyped()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaContent = "done",
                    IsLast = true,
                },
            ],
        ]);
        var actor = CreateActor("resp_1", services => services.AddSingleton<ILLMProviderFactory>(provider));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });

        var request = BuildRunRequest("resp_1");
        request.ToolContext = NewCommandToolContext(
            requestId: "typed-request",
            scopeId: "typed-scope",
            ownerSubject: "typed-owner",
            responseId: "typed-response",
            token: "typed-token",
            routePreference: "typed-route").ToPayload();

        await DispatchRunRequestedAsync(actor, request);

        var llmRequest = provider.Requests.Should().ContainSingle().Subject;
        llmRequest.Metadata.Should().NotBeNull();
        llmRequest.Metadata!.Should().NotContainKey(LLMRequestMetadataKeys.RequestId);
        llmRequest.Metadata.Should().NotContainKey("scope_id");
        llmRequest.RequestId.Should().Be("resp_1");
        llmRequest.CallerContext!.ScopeId.Should().Be("typed-scope");
        llmRequest.ToolContext!.Caller.ScopeId.Should().Be("typed-scope");
        llmRequest.LlmControl!.NyxIdRoutePreference.Should().Be("typed-route");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_WhenProviderThrows_ShouldRecordFailedCompletion()
    {
        var provider = new ThrowingLlmProviderFactory(
            new NyxIdAuthenticationRequiredException("nyxid"));
        var actor = CreateActor("resp_1", services => services.AddSingleton<ILLMProviderFactory>(provider));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });

        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_1"));

        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Failed);
        actor.State.ActiveRun.Should().NotBeNull();
        actor.State.ActiveRun!.Status.Should().Be(3);
        actor.State.ActiveRun.FailureCode.Should().Be("authentication_required");
        actor.State.Completion.Should().NotBeNull();
        actor.State.Completion!.FailureCode.Should().Be("authentication_required");
        actor.State.Completion.FailureMessage.Should().Contain("NyxID authentication required");
    }

    [Fact]
    public async Task HandleLlmRunRequestedAsync_WhenProviderCancels_ShouldRecordCancelledCompletion()
    {
        var provider = new ThrowingLlmProviderFactory(new OperationCanceledException());
        var actor = CreateActor("resp_1", services => services.AddSingleton<ILLMProviderFactory>(provider));
        await actor.HandleRegisterAsync(new RegisterResponseSessionRequested
        {
            Record = BuildRecord("resp_1"),
        });

        await DispatchRunRequestedAsync(actor, BuildRunRequest("resp_1"));

        actor.State.Record!.Status.Should().Be(LlmSessionStatus.Cancelled);
        actor.State.Record.CancelledAt.Should().NotBeNull();
        actor.State.ActiveRun.Should().NotBeNull();
        actor.State.ActiveRun!.Status.Should().Be(4);
        actor.State.Completion.Should().NotBeNull();
        actor.State.Completion!.FailureCode.Should().Be("request_cancelled");
        actor.State.Completion.FailureMessage.Should().Be("LLM run was cancelled.");
    }

    private static LlmSessionGAgent CreateActor(
        string responseId,
        Action<IServiceCollection>? configureServices = null) =>
        GAgentServiceTestKit.CreateStatefulAgent<LlmSessionGAgent, LlmSessionState>(
            new InMemoryEventStore(),
            "response-session-actor-" + responseId,
            static () => throw new InvalidOperationException("LlmSessionGAgent test construction is owned by GAgentServiceTestKit."),
            configureServices);

    private static LlmSessionGAgent CreateActorWithStore(
        string responseId,
        InMemoryEventStore eventStore,
        Action<IServiceCollection>? configureServices = null) =>
        GAgentServiceTestKit.CreateStatefulAgent<LlmSessionGAgent, LlmSessionState>(
            eventStore,
            "response-session-actor-" + responseId,
            static () => throw new InvalidOperationException("LlmSessionGAgent test construction is owned by GAgentServiceTestKit."),
            configureServices);

    private static Task DispatchRunRequestedAsync(LlmSessionGAgent actor, LlmRunRequested command) =>
        actor.HandleEventAsync(new EventEnvelope
        {
            Id = "run-" + command.ResponseId,
            Payload = Any.Pack(command),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = command.ResponseId,
            },
        });

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

    private static LlmRunRequested BuildRunRequest(
        string responseId,
        LlmSessionRuntimeToolSelection? selection = null) =>
        new()
        {
            ResponseId = responseId,
            RunId = "run_1",
            ScopeId = "user-1",
            OwnerSubject = "user-1",
            BearerToken = "token-1",
            Model = "test-model",
            ToolSelection = selection,
            Messages =
            {
                new LlmSessionRuntimeChatMessage
                {
                    Role = "user",
                    Content = "What is the weather?",
                },
            },
        };

    private static StateEvent StateEvent(long version, Google.Protobuf.IMessage payload) =>
        new()
        {
            EventId = $"event-{version}",
            Version = version,
            EventData = Any.Pack(payload),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:00+00:00")),
        };

    private static AgentToolExecutionContext NewCommandToolContext(
        string requestId,
        string scopeId,
        string ownerSubject,
        string responseId,
        string token,
        string routePreference) =>
        AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(requestId, null),
            Credentials = new AgentToolCredentials(token, null, null),
            Caller = new AgentToolCallerContext(scopeId, ownerSubject, responseId),
            Routing = new LLMRequestRoutingContext(null, routePreference, null, null),
        };

    private static LlmSessionRuntimeToolSelection BuildForwardedSelection() =>
        new()
        {
            ForwardedTools =
            {
                new LlmSessionRuntimeToolDeclaration
                {
                    ToolName = "get_weather",
                    Description = "Get weather",
                    ParametersJson = """{"type":"object"}""",
                    Parameters = new Struct
                    {
                        Fields =
                        {
                            ["type"] = Google.Protobuf.WellKnownTypes.Value.ForString("object"),
                        },
                    },
                    SchemaHash = "schema-1",
                },
            },
        };

    private sealed class ScriptedLlmProviderFactory(
        IReadOnlyList<IReadOnlyList<LLMStreamChunk>> responses) : ILLMProviderFactory, ILLMProvider
    {
        private int _nextResponseIndex;

        public List<LLMRequest> Requests { get; } = [];
        public List<AgentToolExecutionContext?> StreamContexts { get; } = [];

        public string Name => "test";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            StreamContexts.Add(AgentToolRequestContext.Current);
            var response = responses[Math.Min(_nextResponseIndex, responses.Count - 1)];
            _nextResponseIndex++;
            foreach (var chunk in response)
            {
                ct.ThrowIfCancellationRequested();
                yield return chunk;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class ThrowingLlmProviderFactory(Exception exception) : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "test";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            throw exception;
            #pragma warning disable CS0162
            yield return new LLMStreamChunk();
            #pragma warning restore CS0162
        }
    }

    private sealed class StaticResponsesToolProvider(
        IReadOnlyList<IAgentTool>? substituteTools = null,
        IReadOnlyList<IAgentTool>? additiveTools = null) : IResponsesToolProvider
    {
        public List<ResponsesToolProviderContext> SubstituteContexts { get; } = [];
        public List<ResponsesToolProviderContext> AdditiveContexts { get; } = [];

        public ValueTask<IReadOnlyList<IAgentTool>> GetSubstituteToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default)
        {
            SubstituteContexts.Add(context);
            return ValueTask.FromResult(substituteTools ?? []);
        }

        public ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default)
        {
            AdditiveContexts.Add(context);
            return ValueTask.FromResult(additiveTools ?? []);
        }
    }

    private sealed class FaultingResponsesToolProvider : IResponsesToolProvider
    {
        public ValueTask<IReadOnlyList<IAgentTool>> GetSubstituteToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default) =>
            ValueTask.FromException<IReadOnlyList<IAgentTool>>(
                new InvalidOperationException("substitute discovery failed"));

        public ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default) =>
            ValueTask.FromException<IReadOnlyList<IAgentTool>>(
                new InvalidOperationException("additive discovery failed"));
    }

    private sealed class RecordingAgentTool(string name, string resultJson) : IAgentTool
    {
        public List<string> Executions { get; } = [];
        public List<AgentToolExecutionContext?> ExecutionContexts { get; } = [];

        public string Name { get; } = name;

        public string Description => "test tool";

        public string ParametersSchema => """{"type":"object"}""";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            Executions.Add(argumentsJson);
            ExecutionContexts.Add(AgentToolRequestContext.Current);
            return Task.FromResult(resultJson);
        }
    }

    private sealed class ThrowingAgentTool(string name, Exception exception) : IAgentTool
    {
        public string Name { get; } = name;

        public string Description => "throwing test tool";

        public string ParametersSchema => """{"type":"object"}""";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromException<string>(exception);
    }

    private sealed class NoOpLlmRunExecutor : ILlmRunExecutor, ILlmRunExecutionService
    {
        public Task<DispatchAdmission> StartAsync(
            LlmRunExecutorRequest request,
            CancellationToken ct = default)
        {
            _ = ct;
            var envelope = new EventEnvelope
            {
                Id = $"start-{request.ResponseId}",
                Payload = Any.Pack(new RecordLlmRunStarted
                {
                    Command = request.Command.Clone(),
                    StartedAt = request.Command.RequestedAt?.Clone(),
                }),
            };
            return Task.FromResult(DispatchAdmissionFactory.Create(request.SessionActorId, envelope));
        }

        public Task ExecuteAsync(
            LlmRunExecutionRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            _ = ct;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLlmRunExecutor(List<string>? observations = null) : ILlmRunExecutor, ILlmRunExecutionService
    {
        public List<LlmRunExecutorRequest> StartRequests { get; } = [];

        public List<LlmRunExecutionRequest> ExecuteRequests { get; } = [];

        public Task<DispatchAdmission> StartAsync(
            LlmRunExecutorRequest request,
            CancellationToken ct = default)
        {
            _ = ct;
            StartRequests.Add(request);
            observations?.Add("executor:start");
            var envelope = new EventEnvelope
            {
                Id = $"start-{request.ResponseId}",
                Payload = Any.Pack(new RecordLlmRunStarted
                {
                    Command = request.Command.Clone(),
                    StartedAt = request.Command.RequestedAt?.Clone(),
                }),
            };
            return Task.FromResult(DispatchAdmissionFactory.Create(request.SessionActorId, envelope));
        }

        public Task ExecuteAsync(
            LlmRunExecutionRequest request,
            CancellationToken ct = default)
        {
            _ = ct;
            ExecuteRequests.Add(request);
            observations?.Add("executor:execute");
            return Task.CompletedTask;
        }
    }

    private class RecordingRuntimeCallbackScheduler(List<string>? observations = null) : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> TimeoutRequests { get; } = [];

        public virtual Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TimeoutRequests.Add(CloneRequest(request));
            observations?.Add("schedule:" + request.CallbackId);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                TimeoutRequests.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private static RuntimeCallbackTimeoutRequest CloneRequest(RuntimeCallbackTimeoutRequest request) =>
            new()
            {
                ActorId = request.ActorId,
                CallbackId = request.CallbackId,
                TriggerEnvelope = request.TriggerEnvelope.Clone(),
                DueTime = request.DueTime,
                DeliveryMode = request.DeliveryMode,
            };
    }

    private sealed class RunTimeoutFailingRuntimeCallbackScheduler : RecordingRuntimeCallbackScheduler
    {
        public override Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            if (request.CallbackId.StartsWith("llm-run-timeout:", StringComparison.Ordinal))
            {
                TimeoutRequests.Add(new RuntimeCallbackTimeoutRequest
                {
                    ActorId = request.ActorId,
                    CallbackId = request.CallbackId,
                    TriggerEnvelope = request.TriggerEnvelope.Clone(),
                    DueTime = request.DueTime,
                    DeliveryMode = request.DeliveryMode,
                });
                throw new InvalidOperationException("synthetic scheduler failure.");
            }

            return base.ScheduleTimeoutAsync(request, ct);
        }
    }
}
