using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Presentation.AGUI;
using FluentAssertions;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScopeServiceEndpointsStreamTests
{
    [Theory]
    [InlineData(AGUIEvent.EventOneofCase.TextMessageEnd, true)]
    [InlineData(AGUIEvent.EventOneofCase.RunError, false)]
    [InlineData(AGUIEvent.EventOneofCase.RunFinished, false)]
    public void ShouldEmitSyntheticRunFinished_ShouldRespectTerminalEvent(
        AGUIEvent.EventOneofCase terminalEventCase,
        bool expected)
    {
        ScopeServiceEndpoints.ShouldEmitSyntheticRunFinished(terminalEventCase)
            .Should()
            .Be(expected);
    }
}
