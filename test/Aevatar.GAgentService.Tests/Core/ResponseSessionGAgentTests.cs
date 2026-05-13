using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ResponseSessionGAgentTests
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
        actor.State.Record.Status.Should().Be(ResponseSessionStatus.Accepted);
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
            Status = ResponseSessionStatus.Cancelled,
        });

        actor.State.Record!.Status.Should().Be(ResponseSessionStatus.Cancelled);
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
            Status = ResponseSessionStatus.Completed,
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
        call.Status.Should().Be(ResponseSessionForwardedToolCallStatus.Pending);
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
        call.Status.Should().Be(ResponseSessionForwardedToolCallStatus.Received);
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
        call.Status.Should().Be(ResponseSessionForwardedToolCallStatus.Resolved);
        call.ResolvedAt.Should().NotBeNull();
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
            Status = ResponseSessionStatus.Cancelled,
        });

        actor.State.ForwardedToolCalls.Should().ContainSingle()
            .Which.Status.Should().Be(ResponseSessionForwardedToolCallStatus.Cancelled);
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

        actor.State.Record!.Status.Should().Be(ResponseSessionStatus.Expired);
        var call = actor.State.ForwardedToolCalls.Should().ContainSingle().Which;
        call.Status.Should().Be(ResponseSessionForwardedToolCallStatus.Expired);
        // The "tool_call_expired" envelope is synthesized by the query reader at
        // the read boundary, not by the actor.
        call.Result.Should().BeNull();
        call.ReceivedAt.Should().NotBeNull();
    }

    private static ResponseSessionGAgent CreateActor(string responseId) =>
        GAgentServiceTestKit.CreateStatefulAgent<ResponseSessionGAgent, ResponseSessionState>(
            new InMemoryEventStore(),
            "response-session-actor-" + responseId,
            static () => new ResponseSessionGAgent());

    private static ResponseSessionRecord BuildRecord(string responseId) =>
        new()
        {
            ResponseId = responseId,
            ScopeId = "user-1",
            OwnerSubject = "user-1",
            OriginKind = ResponseSessionOriginKind.ApiKey,
            PreviousResponseId = string.Empty,
            Status = ResponseSessionStatus.Unspecified,
            CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            Ttl = Duration.FromTimeSpan(TimeSpan.FromHours(24)),
        };

    private static ResponseSessionForwardedToolCall BuildToolCall(string callId) =>
        new()
        {
            CallId = callId,
            ToolName = "get_weather",
            SchemaHash = "schema-1",
            Arguments = ResponsesJsonValues.ParseBoundaryPayload("""{"city":"Singapore"}"""),
            Status = ResponseSessionForwardedToolCallStatus.Pending,
            EmittedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            Expiry = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(1)),
        };
}
