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
    public void DeepCloneValue_ShouldCloneListAndNullStudioStepParameterValues()
    {
        var original = StudioStepParameterValue.FromList(
        [
            StudioStepParameterValue.FromScalar("value"),
            null,
        ]);

        var clone = original.DeepCloneValue();

        clone.Should().NotBeSameAs(original);
        clone.ToPlainValue().Should().BeEquivalentTo(new List<object?> { "value", null });
        StudioStepParameterValue.Null.DeepCloneValue().Should().BeSameAs(StudioStepParameterValue.Null);
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
        StudioStepParameterValue.FromScalar(null).ToWorkflowScalarString().Should().BeNull();
        StudioStepParameterValue.FromPlainValue(true).ToWorkflowScalarString().Should().Be("true");
        StudioStepParameterValue.FromPlainValue(42).ToWorkflowScalarString().Should().Be("42");
        new EmptyDisplayStudioStepParameterValue().ToString().Should().BeEmpty();
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

    [Fact]
    public void FromPlainValue_ShouldMapStudioStepParameterObjectShapes()
    {
        var typedPropertyValue = StudioStepParameterValue.FromPlainValue(
            new List<KeyValuePair<string, StudioStepParameterValue?>>
            {
                new("typed", StudioStepParameterValue.FromScalar("value")),
                new("missing", null),
            });
        var plainPropertyValue = StudioStepParameterValue.FromPlainValue(
            new List<KeyValuePair<string, object?>>
            {
                new("text", "value"),
                new("count", 3),
                new("missing", null),
            });

        typedPropertyValue.ToPlainValue().Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["typed"] = "value",
            ["missing"] = null,
        });
        plainPropertyValue.ToPlainValue().Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["text"] = "value",
            ["count"] = "3",
            ["missing"] = null,
        });
    }

    [Fact]
    public void FromPlainValue_ShouldMapStudioStepParameterListShapes()
    {
        var typedItemValue = StudioStepParameterValue.FromPlainValue(
            new List<StudioStepParameterValue?>
            {
                StudioStepParameterValue.FromScalar("value"),
                null,
            });
        var plainItemValue = StudioStepParameterValue.FromPlainValue(new List<object?> { "text", 5, null });

        typedItemValue.ToPlainValue().Should().BeEquivalentTo(new List<object?> { "value", null });
        plainItemValue.ToPlainValue().Should().BeEquivalentTo(new List<object?> { "text", "5", null });
    }

    [Fact]
    public void FromPlainValue_ShouldCloneStudioStepParameterValueAndFallbackToString()
    {
        var source = StudioStepParameterValue.FromObject(
        [
            new KeyValuePair<string, StudioStepParameterValue?>("key", StudioStepParameterValue.FromScalar("value")),
        ]);

        var cloned = StudioStepParameterValue.FromPlainValue(source);
        var fallback = StudioStepParameterValue.FromPlainValue(new Uri("https://example.test/path"));

        cloned.Should().NotBeSameAs(source);
        cloned.ToPlainValue().Should().BeEquivalentTo(source.ToPlainValue());
        fallback.ToWorkflowScalarString().Should().Be("https://example.test/path");
    }

    [Fact]
    public void StudioStepParameters_ShouldPreserveOrdinalKeysAndDeepCloneValues()
    {
        var fromDictionary = new StudioStepParameters(new Dictionary<string, StudioStepParameterValue?>
        {
            ["Key"] = StudioStepParameterValue.FromScalar("upper"),
        });
        var fromPairs = new StudioStepParameters(
        [
            new KeyValuePair<string, StudioStepParameterValue?>("key", StudioStepParameterValue.FromScalar("lower")),
            new KeyValuePair<string, StudioStepParameterValue?>("missing", null),
        ]);

        fromDictionary.ContainsKey("key").Should().BeFalse();
        fromPairs.ContainsKey("Key").Should().BeFalse();

        var clone = fromPairs.DeepCloneParameters();
        clone.Should().NotBeSameAs(fromPairs);
        clone["key"].Should().NotBeSameAs(fromPairs["key"]);
        clone["key"]!.ToWorkflowScalarString().Should().Be("lower");
        clone["missing"].Should().BeNull();
    }

    private sealed record EmptyDisplayStudioStepParameterValue : StudioStepParameterValue
    {
        public override bool IsComplexValue() => false;

        public override string? ToWorkflowScalarString() => null;

        public override object? ToPlainValue() => null;

        public override StudioStepParameterValue DeepCloneValue() => this;

        public override string ToString() => base.ToString();
    }
}
