using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Application.Services;
using Aevatar.AppPlatform.Infrastructure.Stores;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Aevatar.AppPlatform.Tests;

public class OperationApplicationServiceTests
{
    [Fact]
    public async Task AdvanceAsync_ShouldPersistTerminalResultAndHistory()
    {
        var store = new InMemoryOperationStore();
        var commandPort = new OperationCommandApplicationService(store);
        var queryPort = new OperationQueryApplicationService(store);

        var accepted = await commandPort.AcceptAsync(new AppOperationSnapshot
        {
            Kind = AppOperationKind.FunctionInvoke,
            AppId = "copilot",
            ReleaseId = "prod",
            FunctionId = "default-chat",
            ServiceId = "chat-gateway",
            EndpointId = "chat",
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
        });

        await commandPort.AdvanceAsync(new AppOperationUpdate
        {
            OperationId = accepted.OperationId,
            Status = AppOperationStatus.Running,
            EventCode = "running",
            Message = "Operation is running.",
        });

        await commandPort.AdvanceAsync(new AppOperationUpdate
        {
            OperationId = accepted.OperationId,
            Status = AppOperationStatus.Completed,
            EventCode = "completed",
            Message = "Operation completed.",
            Result = new AppOperationResult
            {
                ResultCode = "completed",
                Message = "Operation completed.",
                Payload = Any.Pack(new StringValue { Value = "hello" }),
            },
        });

        var snapshot = await queryPort.GetAsync(accepted.OperationId);
        var result = await queryPort.GetResultAsync(accepted.OperationId);
        var events = await queryPort.ListEventsAsync(accepted.OperationId);

        snapshot.Should().NotBeNull();
        snapshot!.Status.Should().Be(AppOperationStatus.Completed);

        result.Should().NotBeNull();
        result!.Status.Should().Be(AppOperationStatus.Completed);
        result.ResultCode.Should().Be("completed");
        result.Payload.Unpack<StringValue>().Value.Should().Be("hello");

        events.Select(x => x.EventCode).Should().Equal("accepted", "running", "completed");
        events[^1].Status.Should().Be(AppOperationStatus.Completed);
    }

    [Fact]
    public async Task WatchAsync_ShouldYieldAcceptedAndFutureTerminalEvent()
    {
        var store = new InMemoryOperationStore();
        var commandPort = new OperationCommandApplicationService(store);
        var queryPort = new OperationQueryApplicationService(store);

        var accepted = await commandPort.AcceptAsync(new AppOperationSnapshot
        {
            Kind = AppOperationKind.FunctionInvoke,
            AppId = "copilot",
            ReleaseId = "prod",
            FunctionId = "default-chat",
            ServiceId = "chat-gateway",
            EndpointId = "chat",
        });

        await using var enumerator = queryPort.WatchAsync(accepted.OperationId).GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.EventCode.Should().Be("accepted");

        var nextEventTask = enumerator.MoveNextAsync().AsTask();
        await commandPort.AdvanceAsync(new AppOperationUpdate
        {
            OperationId = accepted.OperationId,
            Status = AppOperationStatus.Cancelled,
            EventCode = "cancelled",
            Message = "Operation cancelled.",
        });

        (await nextEventTask).Should().BeTrue();
        enumerator.Current.EventCode.Should().Be("cancelled");
        enumerator.Current.Status.Should().Be(AppOperationStatus.Cancelled);

        (await enumerator.MoveNextAsync()).Should().BeFalse();
    }
}
