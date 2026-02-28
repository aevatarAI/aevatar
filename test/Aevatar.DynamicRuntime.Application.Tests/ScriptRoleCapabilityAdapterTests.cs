using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Core.Adapters;
using FluentAssertions;
using Xunit;

namespace Aevatar.DynamicRuntime.Application.Tests;

public sealed class ScriptRoleCapabilityAdapterTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldInvokeEntrypoint()
    {
        var adapter = new ScriptRoleCapabilityAdapter(
            new FakeEntrypoint(),
            new ScriptRoleCapabilitySnapshot("svc.adapter", "v1", "FakeEntrypoint", DynamicServiceMode.Hybrid, "cap:v1"));

        var result = await adapter.ExecuteAsync("hello");

        result.Should().Be("adapter:hello");
    }

    private sealed class FakeEntrypoint : IScriptRoleEntrypoint
    {
        public Task<string> HandleAsync(string input, CancellationToken ct = default)
            => Task.FromResult($"adapter:{input}");
    }
}
