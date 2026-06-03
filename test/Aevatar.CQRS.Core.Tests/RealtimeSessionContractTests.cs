using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Core.Tests;

public sealed class RealtimeSessionContractTests
{
    [Fact]
    public void CommandInteractionService_ShouldResolveSameInstanceAsRealtimeSession()
    {
        var expected = new RecordingCommandInteractionService();
        var services = new ServiceCollection();
        services.AddSingleton<ICommandInteractionService<string, string, string, string, string>>(expected);
        services.AddSingleton<IRealtimeSession<string, string, string, string, string>>(sp =>
            sp.GetRequiredService<ICommandInteractionService<string, string, string, string, string>>());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IRealtimeSession<string, string, string, string, string>>()
            .Should().BeSameAs(provider.GetRequiredService<ICommandInteractionService<string, string, string, string, string>>());
    }

    [Fact]
    public async Task CommandInteractionService_ShouldExposeAcceptedBeforeFramesThroughRealtimeContract()
    {
        ICommandInteractionService<string, string, string, string, string> service =
            new RecordingCommandInteractionService();
        var realtime = (IRealtimeSession<string, string, string, string, string>)service;
        var order = new List<string>();

        var result = await realtime.ExecuteAsync(
            "command",
            (frame, _) =>
            {
                order.Add("frame:" + frame);
                return ValueTask.CompletedTask;
            },
            (receipt, _) =>
            {
                order.Add("accepted:" + receipt);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be("receipt");
        result.Completed.Should().BeTrue();
        result.Completion.Should().Be("completed");
        order.Should().Equal("accepted:receipt", "frame:terminal");
    }

    private sealed class RecordingCommandInteractionService
        : ICommandInteractionService<string, string, string, string, string>
    {
        public async Task<CommandInteractionResult<string, string, string>> ExecuteAsync(
            string command,
            Func<string, CancellationToken, ValueTask> emitAsync,
            Func<string, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            command.Should().Be("command");
            if (onAcceptedAsync != null)
                await onAcceptedAsync("receipt", ct);
            await emitAsync("terminal", ct);

            return CommandInteractionResult<string, string, string>.Success(
                "receipt",
                new CommandInteractionFinalizeResult<string>("completed", true));
        }

        async Task<RealtimeSessionResult<string, string, string>> IRealtimeSession<string, string, string, string, string>.ExecuteAsync(
            string inbound,
            Func<string, CancellationToken, ValueTask> emitAsync,
            Func<string, CancellationToken, ValueTask>? onAcceptedAsync,
            CancellationToken ct)
        {
            return await ExecuteAsync(inbound, emitAsync, onAcceptedAsync, ct);
        }
    }
}
