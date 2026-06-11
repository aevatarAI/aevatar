using System.Globalization;
using System.Text.Json;

namespace Aevatar.Studio.Domain.Studio.Models;

public abstract record StudioStepParameterValue
{
    public static StudioStepParameterValue Null { get; } = new StudioStepNullParameterValue();

    public static StudioStepParameterValue FromScalar(string? value) =>
        value is null ? Null : new StudioStepScalarParameterValue(value);

    public static StudioStepParameterValue FromList(IEnumerable<StudioStepParameterValue?> items) =>
        new StudioStepListParameterValue(items.Select(item => item?.DeepCloneValue() ?? Null).ToList());

    public static StudioStepParameterValue FromObject(IEnumerable<KeyValuePair<string, StudioStepParameterValue?>> properties) =>
        new StudioStepObjectParameterValue(properties.ToDictionary(
            pair => pair.Key,
            pair => pair.Value?.DeepCloneValue() ?? Null,
            StringComparer.Ordinal));

    public static StudioStepParameterValue FromPlainValue(object? value) =>
        value switch
        {
            null => Null,
            StudioStepParameterValue parameterValue => parameterValue.DeepCloneValue(),
            string text => FromScalar(text),
            bool boolean => FromScalar(boolean ? "true" : "false"),
            IFormattable formattable => FromScalar(formattable.ToString(null, CultureInfo.InvariantCulture)),
            IEnumerable<KeyValuePair<string, StudioStepParameterValue?>> typedProperties => FromObject(typedProperties),
            IEnumerable<KeyValuePair<string, object?>> plainProperties => FromObject(
                plainProperties.Select(pair => new KeyValuePair<string, StudioStepParameterValue?>(
                    pair.Key,
                    FromPlainValue(pair.Value)))),
            IEnumerable<StudioStepParameterValue?> typedItems => FromList(typedItems),
            IEnumerable<object?> plainItems => FromList(plainItems.Select(FromPlainValue)),
            _ => FromScalar(value.ToString()),
        };

    public abstract bool IsComplexValue();

    public abstract string? ToWorkflowScalarString();

    public abstract object? ToPlainValue();

    public abstract StudioStepParameterValue DeepCloneValue();

    public override string ToString() => ToWorkflowScalarString() ?? string.Empty;
}

public sealed record StudioStepNullParameterValue : StudioStepParameterValue
{
    public override bool IsComplexValue() => false;

    public override string? ToWorkflowScalarString() => null;

    public override object? ToPlainValue() => null;

    public override StudioStepParameterValue DeepCloneValue() => Null;
}

public sealed record StudioStepScalarParameterValue(string Scalar) : StudioStepParameterValue
{
    public override bool IsComplexValue() => false;

    public override string ToWorkflowScalarString() => Scalar;

    public override object ToPlainValue() => Scalar;

    public override StudioStepParameterValue DeepCloneValue() => FromScalar(Scalar);
}

public sealed record StudioStepListParameterValue(IReadOnlyList<StudioStepParameterValue> Items) : StudioStepParameterValue
{
    public override bool IsComplexValue() => true;

    public override string ToWorkflowScalarString() => JsonSerializer.Serialize(ToPlainValue());

    public override object ToPlainValue() => Items.Select(item => item.ToPlainValue()).ToList();

    public override StudioStepParameterValue DeepCloneValue() => FromList(Items);
}

public sealed record StudioStepObjectParameterValue(
    IReadOnlyDictionary<string, StudioStepParameterValue> Properties) : StudioStepParameterValue
{
    public override bool IsComplexValue() => true;

    public override string ToWorkflowScalarString() => JsonSerializer.Serialize(ToPlainValue());

    public override object ToPlainValue() => Properties.ToDictionary(
        property => property.Key,
        property => property.Value.ToPlainValue(),
        StringComparer.Ordinal);

    public override StudioStepParameterValue DeepCloneValue() => FromObject(Properties.Select(property =>
        new KeyValuePair<string, StudioStepParameterValue?>(property.Key, property.Value)));
}
