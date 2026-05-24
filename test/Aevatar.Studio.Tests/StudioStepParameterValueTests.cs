using Aevatar.Studio.Domain.Studio.Models;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioStepParameterValueTests
{
    [Fact]
    public void DeepCloneValue_ShouldCloneObject()
    {
        var original = StudioStepParameterValue.FromObject(
        [
            new KeyValuePair<string, StudioStepParameterValue?>("key", StudioStepParameterValue.FromScalar("value")),
        ]);

        var clone = original.DeepCloneValue();

        clone.Should().NotBeSameAs(original);
        clone.ToWorkflowScalarString().Should().Be(original.ToWorkflowScalarString());
    }

    [Fact]
    public void IsComplexValue_ShouldReturnTrueForObjectAndList()
    {
        StudioStepParameterValue.FromObject([]).IsComplexValue().Should().BeTrue();
        StudioStepParameterValue.FromList([]).IsComplexValue().Should().BeTrue();
    }

    [Fact]
    public void IsComplexValue_ShouldReturnFalseForScalarAndNull()
    {
        StudioStepParameterValue.FromScalar("hello").IsComplexValue().Should().BeFalse();
        StudioStepParameterValue.Null.IsComplexValue().Should().BeFalse();
    }

    [Fact]
    public void ToWorkflowScalarString_ShouldReturnScalarValues()
    {
        StudioStepParameterValue.FromScalar("hello").ToWorkflowScalarString().Should().Be("hello");
        StudioStepParameterValue.FromPlainValue(true).ToWorkflowScalarString().Should().Be("true");
        StudioStepParameterValue.FromPlainValue(42).ToWorkflowScalarString().Should().Be("42");
    }

    [Fact]
    public void ToWorkflowScalarString_ShouldReturnJsonForComplexValues()
    {
        var obj = StudioStepParameterValue.FromObject(
        [
            new KeyValuePair<string, StudioStepParameterValue?>("key", StudioStepParameterValue.FromScalar("value")),
        ]);
        var array = StudioStepParameterValue.FromList(
        [
            StudioStepParameterValue.FromScalar("1"),
            StudioStepParameterValue.FromScalar("2"),
        ]);

        obj.ToWorkflowScalarString().Should().Contain("\"key\"");
        array.ToWorkflowScalarString().Should().Contain("1");
    }

    [Fact]
    public void ToPlainValue_ShouldReturnDictionaryListAndScalar()
    {
        var obj = StudioStepParameterValue.FromObject(
        [
            new KeyValuePair<string, StudioStepParameterValue?>("key", StudioStepParameterValue.FromScalar("value")),
        ]);
        var array = StudioStepParameterValue.FromList(
        [
            StudioStepParameterValue.FromScalar("a"),
            StudioStepParameterValue.FromScalar("b"),
        ]);

        obj.ToPlainValue().Should().BeOfType<Dictionary<string, object?>>();
        array.ToPlainValue().Should().BeOfType<List<object?>>();
        StudioStepParameterValue.FromScalar("hello").ToPlainValue().Should().Be("hello");
        StudioStepParameterValue.Null.ToPlainValue().Should().BeNull();
    }
}
