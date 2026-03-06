using Aevatar.Scripting.Application.AI;
using Aevatar.Scripting.Core.AI;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.AI;

public class RoleAgentDelegateAICapabilityTests
{
    [Fact]
    public async Task AskAsync_ShouldDelegateToRolePort()
    {
        var capability = new RoleAgentDelegateAICapability(new FakeRoleAgentPort("ok"));

        var output = await capability.AskAsync("run-1", "corr-1", "hello", CancellationToken.None);

        output.Should().Be("ok");
    }

    private sealed class FakeRoleAgentPort(string output) : IRoleAgentPort
    {
        public Task<string> RunAsync(
            string runId,
            string correlationId,
            string prompt,
            CancellationToken ct)
        {
            runId.Should().Be("run-1");
            correlationId.Should().Be("corr-1");
            prompt.Should().Be("hello");
            return Task.FromResult(output);
        }
    }
}
