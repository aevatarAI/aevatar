using Aevatar.Scripting.Core.AI;
using FluentAssertions;

namespace Aevatar.Scripting.Core.Tests.AI;

public class ClaimRoleIntegrationTests
{
    [Fact]
    public async Task Should_delegate_to_role_agent_capability_with_correlation()
    {
        var rolePort = new RecordingRoleAgentPort();
        var capability = new RoleAgentDelegateAICapability(rolePort);

        var output = await capability.AskAsync(
            runId: "run-claim-1",
            correlationId: "corr-claim-1",
            prompt: "extract claim facts",
            ct: CancellationToken.None);

        output.Should().Be("structured-facts");
        rolePort.RunId.Should().Be("run-claim-1");
        rolePort.CorrelationId.Should().Be("corr-claim-1");
        rolePort.Prompt.Should().Be("extract claim facts");
    }

    private sealed class RecordingRoleAgentPort : IRoleAgentPort
    {
        public string RunId { get; private set; } = string.Empty;
        public string CorrelationId { get; private set; } = string.Empty;
        public string Prompt { get; private set; } = string.Empty;

        public Task<string> RunAsync(
            string runId,
            string correlationId,
            string prompt,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RunId = runId;
            CorrelationId = correlationId;
            Prompt = prompt;
            return Task.FromResult("structured-facts");
        }
    }
}
