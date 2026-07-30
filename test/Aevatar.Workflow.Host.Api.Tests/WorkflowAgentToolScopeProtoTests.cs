using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowAgentToolScopeProtoTests
{
    [Fact]
    public void ToolSetRefs_ShouldRoundtripSeparatelyFromAllowedToolNames()
    {
        var scope = new WorkflowAgentToolScope
        {
            RestrictAllowedToolNames = true,
            RestrictToolSets = true,
            AllowedToolNames = { "search" },
            ToolSetRefs = { "nyxid.connected_services" },
        };

        var parsed = WorkflowAgentToolScope.Parser.ParseFrom(scope.ToByteArray());

        parsed.AllowedToolNames.Should().Equal("search");
        parsed.ToolSetRefs.Should().Equal("nyxid.connected_services");
        parsed.RestrictAllowedToolNames.Should().BeTrue();
        parsed.RestrictToolSets.Should().BeTrue();
    }
}
