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

    [Fact]
    public void CreateWorkflowStepSessionId_WithRunScopeAttempt_ShouldReturnCanonicalKey()
    {
        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId("scope-1", "run-1", "step-1", attempt: 3);

        sessionId.Should().Be("scope-1:run-1:step-1:a3");
    }

    [Theory]
    [InlineData(null, "run", "step", 1)]
    [InlineData("", "run", "step", 1)]
    [InlineData("scope", null, "step", 1)]
    [InlineData("scope", "", "step", 1)]
    [InlineData("scope", "run", null, 1)]
    [InlineData("scope", "run", "", 1)]
    public void CreateWorkflowStepSessionId_WithInvalidCompositeInputs_ShouldThrowArgumentException(
        string? scopeId,
        string? runId,
        string? stepId,
        int attempt)
    {
        Action act = () => ChatSessionKeys.CreateWorkflowStepSessionId(scopeId!, runId!, stepId!, attempt);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateWorkflowStepSessionId_WithAttemptBelowOne_ShouldThrowArgumentOutOfRangeException()
    {
        Action act = () => ChatSessionKeys.CreateWorkflowStepSessionId("scope", "run", "step", attempt: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
