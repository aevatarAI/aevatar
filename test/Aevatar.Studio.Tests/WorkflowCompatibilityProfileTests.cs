using Aevatar.Studio.Domain.Studio.Compatibility;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowCompatibilityProfileTests
{
    private readonly WorkflowCompatibilityProfile _profile = WorkflowCompatibilityProfile.AevatarV1;

    [Fact]
    public void AevatarV1_ShouldHaveExpectedVersion()
    {
        _profile.Version.Should().Be("aevatar.workflow.v1");
    }

    [Theory]
    [InlineData("loop", "while")]
    [InlineData("sub_workflow", "workflow_call")]
    [InlineData("foreach_llm", "foreach")]
    [InlineData("http_get", "connector_call")]
    [InlineData("http_post", "connector_call")]
    [InlineData("mcp_call", "connector_call")]
    [InlineData("sleep", "delay")]
    [InlineData("publish", "emit")]
    [InlineData("vote_consensus", "vote")]
    public void ToCanonicalType_ShouldResolveAliases(string alias, string expected)
    {
        _profile.ToCanonicalType(alias).Should().Be(expected);
    }

    [Theory]
    [InlineData("transform")]
    [InlineData("conditional")]
    [InlineData("llm_call")]
    [InlineData("workflow_call")]
    public void ToCanonicalType_ShouldReturnCanonicalAsIs(string type)
    {
        _profile.ToCanonicalType(type).Should().Be(type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ToCanonicalType_ShouldReturnEmptyForNullOrBlank(string? value)
    {
        _profile.ToCanonicalType(value).Should().BeEmpty();
    }

    [Theory]
    [InlineData("  LOOP  ", "while")]
    [InlineData("Transform", "transform")]
    public void ToCanonicalType_ShouldBeCaseInsensitiveAndTrim(string value, string expected)
    {
        _profile.ToCanonicalType(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("transform", true)]
    [InlineData("loop", true)]
    [InlineData("actor_send", true)]
    [InlineData("workflow_loop", true)]
    [InlineData("nonexistent", false)]
    [InlineData(null, false)]
    public void IsKnownStepType_ShouldRecognizeAllRegisteredTypes(string? type, bool expected)
    {
        _profile.IsKnownStepType(type).Should().Be(expected);
    }

    [Theory]
    [InlineData("transform", true)]
    [InlineData("actor_send", false)]
    [InlineData("workflow_loop", false)]
    public void IsCanonicalStepType_ShouldOnlyMatchCanonical(string type, bool expected)
    {
        _profile.IsCanonicalStepType(type).Should().Be(expected);
    }

    [Fact]
    public void IsAdvancedImportOnly_ShouldMatchActorSend()
    {
        _profile.IsAdvancedImportOnly("actor_send").Should().BeTrue();
        _profile.IsAdvancedImportOnly("transform").Should().BeFalse();
    }

    [Fact]
    public void IsForbiddenAuthoringType_ShouldMatchWorkflowLoop()
    {
        _profile.IsForbiddenAuthoringType("workflow_loop").Should().BeTrue();
        _profile.IsForbiddenAuthoringType("while").Should().BeFalse();
    }

    [Theory]
    [InlineData("sub_step_type", true)]
    [InlineData("map_step_type", true)]
    [InlineData("step", true)]
    [InlineData("prompt", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsStepTypeParameterKey_ShouldMatchExpectedKeys(string? key, bool expected)
    {
        _profile.IsStepTypeParameterKey(key).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("singleton", true)]
    [InlineData("transient", true)]
    [InlineData("scope", true)]
    [InlineData("unknown", false)]
    public void IsSupportedWorkflowCallLifecycle_ShouldValidateLifecycles(string? value, bool expected)
    {
        _profile.IsSupportedWorkflowCallLifecycle(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("wait_signal", true)]
    [InlineData("connector_call", true)]
    [InlineData("llm_call", true)]
    [InlineData("human_input", true)]
    [InlineData("human_approval", true)]
    [InlineData("transform", false)]
    [InlineData("conditional", false)]
    public void ShouldMirrorTimeoutMsToParameters_ShouldMatchExpectedTypes(string type, bool expected)
    {
        _profile.ShouldMirrorTimeoutMsToParameters(type).Should().Be(expected);
    }
}
