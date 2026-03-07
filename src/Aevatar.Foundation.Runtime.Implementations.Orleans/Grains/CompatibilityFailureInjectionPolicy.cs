namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

/// <summary>
/// Test-only fault injection helper for the production mixed-version rolling-upgrade feature.
/// The production feature is the runtime's ability to keep processing available during old/new
/// version coexistence and rely on runtime retry for convergence. This policy does not enable
/// mixed-version mode itself; it only injects synthetic old-node failures in CI/staging so the
/// production rollout path can be validated deterministically.
/// </summary>
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

    /// <summary>
    /// Reads the test-only injection switches used to validate the production rolling-upgrade path.
    /// AEVATAR_TEST_NODE_VERSION_TAG / AEVATAR_TEST_FAIL_EVENT_TYPE_URLS must not be treated as the
    /// production mixed-version feature switch; production mixed-version is the default runtime
    /// behavior while old/new binaries coexist in one Orleans cluster during rollout.
    /// </summary>
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
