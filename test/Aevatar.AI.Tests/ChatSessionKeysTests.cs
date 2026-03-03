using Aevatar.AI.Abstractions;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class ChatSessionKeysTests
{
    [Fact]
    public void CreateWorkflowStepSessionId_WithScopeAndStep_ShouldReturnCanonicalKey()
    {
        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId("scope-1", "step-1");

        sessionId.Should().Be("scope-1:step-1");
    }

    [Theory]
    [InlineData(null, "step")]
    [InlineData("", "step")]
    [InlineData("   ", "step")]
    [InlineData("scope", null)]
    [InlineData("scope", "")]
    [InlineData("scope", "   ")]
    public void CreateWorkflowStepSessionId_WithInvalidInputs_ShouldThrow(string? scopeId, string? stepId)
    {
        Action act = () => ChatSessionKeys.CreateWorkflowStepSessionId(scopeId!, stepId!);

        act.Should().Throw<ArgumentException>();
    }

}
