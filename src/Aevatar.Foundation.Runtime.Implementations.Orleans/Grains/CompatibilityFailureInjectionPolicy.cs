namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

internal sealed class CompatibilityFailureInjectionPolicy
{
    private const string OldNodeVersionTag = "old";
    private readonly HashSet<string> _eventTypeUrls;

    private CompatibilityFailureInjectionPolicy(HashSet<string> eventTypeUrls)
    {
        _eventTypeUrls = eventTypeUrls;
    }

    public bool Enabled => _eventTypeUrls.Count > 0;

    public static CompatibilityFailureInjectionPolicy Disabled { get; } =
        new(new HashSet<string>(StringComparer.Ordinal));

    public static CompatibilityFailureInjectionPolicy FromEnvironment()
    {
        var nodeVersion = Environment.GetEnvironmentVariable("AEVATAR_TEST_NODE_VERSION_TAG");
        var rawTypeUrls = Environment.GetEnvironmentVariable("AEVATAR_TEST_FAIL_EVENT_TYPE_URLS");
        return FromValues(nodeVersion, rawTypeUrls);
    }

    internal static CompatibilityFailureInjectionPolicy FromValues(string? nodeVersion, string? rawTypeUrls)
    {
        if (!string.Equals(nodeVersion, OldNodeVersionTag, StringComparison.OrdinalIgnoreCase))
            return Disabled;

        if (string.IsNullOrWhiteSpace(rawTypeUrls))
            return Disabled;

        var eventTypeUrls = rawTypeUrls
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);

        return eventTypeUrls.Count == 0 ? Disabled : new CompatibilityFailureInjectionPolicy(eventTypeUrls);
    }

    public bool ShouldInject(string? eventTypeUrl)
    {
        return Enabled
               && !string.IsNullOrWhiteSpace(eventTypeUrl)
               && _eventTypeUrls.Contains(eventTypeUrl);
    }
}
