using Aevatar.Foundation.Runtime.Actors;
using Shouldly;

namespace Aevatar.Foundation.Core.Tests;

public class ActorDeactivationHookDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_ShouldInvokeAllHooks()
    {
        var calls = new List<string>();
        var dispatcher = new ActorDeactivationHookDispatcher(
        [
            new CallbackHook(id => calls.Add($"h1:{id}")),
            new CallbackHook(id => calls.Add($"h2:{id}")),
        ]);

        await dispatcher.DispatchAsync("actor-1");

        calls.ShouldBe(["h1:actor-1", "h2:actor-1"]);
    }

    [Fact]
    public async Task DispatchAsync_WhenOneHookThrows_ShouldContinue()
    {
        var calls = new List<string>();
        var dispatcher = new ActorDeactivationHookDispatcher(
        [
            new CallbackHook(_ => throw new InvalidOperationException("hook-failed")),
            new CallbackHook(id => calls.Add($"ok:{id}")),
        ]);

        await dispatcher.DispatchAsync("actor-2");

        calls.ShouldBe(["ok:actor-2"]);
    }

    [Fact]
    public async Task DispatchAsync_WhenActorIdIsEmpty_ShouldSkip()
    {
        var called = false;
        var dispatcher = new ActorDeactivationHookDispatcher(
        [
            new CallbackHook(_ => called = true),
        ]);

        await dispatcher.DispatchAsync("");

        called.ShouldBeFalse();
    }

    private sealed class CallbackHook : IActorDeactivationHook
    {
        private readonly Action<string> _callback;

        public CallbackHook(Action<string> callback)
        {
            _callback = callback;
        }

        public Task OnDeactivatedAsync(string actorId, CancellationToken ct = default)
        {
            _callback(actorId);
            return Task.CompletedTask;
        }
    }
}
