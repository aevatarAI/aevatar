namespace Aevatar.Studio.Domain.Studio.Models;

public sealed class StudioStepParameters : Dictionary<string, StudioStepParameterValue?>
{
    public StudioStepParameters()
        : base(StringComparer.Ordinal)
    {
    }

    public StudioStepParameters(IDictionary<string, StudioStepParameterValue?> values)
        : base(values, StringComparer.Ordinal)
    {
    }

    public StudioStepParameters(IEnumerable<KeyValuePair<string, StudioStepParameterValue?>> values)
        : base(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal)
    {
    }

    public StudioStepParameters DeepCloneParameters() =>
        new(this.Select(pair => new KeyValuePair<string, StudioStepParameterValue?>(
            pair.Key,
            pair.Value?.DeepCloneValue())));
}
