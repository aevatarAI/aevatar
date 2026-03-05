using Microsoft.Extensions.Options;

namespace Aevatar.App.Application.Services;

public sealed class FallbackOptions
{
    public List<ManifestationFallbackOptions> Content { get; set; } = [];
    public ManifestationFixedFallbackOptions? FixedContent { get; set; }
    public List<string> Affirmations { get; set; } = [];
    public string? FixedAffirmation { get; set; }
    public string? PlaceholderImage { get; set; }
}

public sealed class ManifestationFallbackOptions
{
    public string Name { get; set; } = string.Empty;
    public string Mantra { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class ManifestationFixedFallbackOptions
{
    public string Name { get; set; } = string.Empty;
    public string MantraTemplate { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class FallbackContent : IFallbackContent
{
    private static readonly IReadOnlyList<ManifestationFallback> DefaultPlants =
    [
        new("Seed of Potential", "I am growing toward my highest self.",
            "A seedling of infinite potential, waiting to bloom into your dreams."),
        new("Bloom of Hope", "Each day brings new opportunities for growth.",
            "A delicate flower representing the hope that lives within your heart."),
        new("Tree of Intention", "My roots grow deeper as my branches reach higher.",
            "A mighty tree grounded in purpose, stretching toward the light of possibility."),
        new("Garden of Dreams", "I nurture my dreams with patience and love.",
            "A mystical garden where your intentions take root and flourish."),
        new("Fern of Focus", "I channel my energy toward what matters most.",
            "Ancient and wise, this fern helps you maintain clarity on your path."),
    ];

    private static readonly ManifestationFixedFallback DefaultFixedContent =
        new("Celestial Potential",
            "I am successfully manifesting: {userGoal}",
            "A beautiful plant full of potential, glowing with an inner light.");

    private static readonly IReadOnlyList<string> DefaultAffirmations =
    [
        "I nurture my dreams with belief and action.",
        "Each drop of water feeds my growing intentions.",
        "My dedication today creates tomorrow's blooming success.",
        "I tend to my goals with patience and love.",
        "Every moment of care brings me closer to flourishing.",
    ];

    private const string DefaultFixedAffirmation = "I nurture my dreams with belief and action.";
    private const string DefaultPlaceholderImage =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private readonly ManifestationFixedFallback _fixedContent;

    public FallbackContent(IOptions<FallbackOptions> options)
    {
        var configured = options.Value ?? new FallbackOptions();

        var configuredPlants = (configured.Content ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Name)
                           && !string.IsNullOrWhiteSpace(item.Mantra)
                           && !string.IsNullOrWhiteSpace(item.Description))
            .Select(item => new ManifestationFallback(
                item.Name.Trim(),
                item.Mantra.Trim(),
                item.Description.Trim()))
            .ToArray();

        Plants = configuredPlants.Length > 0 ? configuredPlants : DefaultPlants;

        _fixedContent = BuildFixedContent(configured.FixedContent) ?? DefaultFixedContent;

        var configuredAffirmations = (configured.Affirmations ?? [])
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Affirmations = configuredAffirmations.Length > 0 ? configuredAffirmations : DefaultAffirmations;

        FixedAffirmation = string.IsNullOrWhiteSpace(configured.FixedAffirmation)
            ? DefaultFixedAffirmation
            : configured.FixedAffirmation.Trim();

        PlaceholderImage = IsBase64(configured.PlaceholderImage)
            ? configured.PlaceholderImage!
            : DefaultPlaceholderImage;
    }

    public IReadOnlyList<ManifestationFallback> Plants { get; }
    public IReadOnlyList<string> Affirmations { get; }
    public string FixedAffirmation { get; }
    public string PlaceholderImage { get; }

    public ManifestationFallback GetManifestationFallback()
        => Plants[Random.Shared.Next(Plants.Count)];

    public ManifestationFallback GetManifestationFixedFallback(string userGoal)
    {
        var safeGoal = userGoal ?? string.Empty;
        var mantra = _fixedContent.MantraTemplate.Replace("{userGoal}", safeGoal, StringComparison.Ordinal);
        return new ManifestationFallback(_fixedContent.PlantName, mantra, _fixedContent.PlantDescription);
    }

    public string GetAffirmationFallback()
        => Affirmations[Random.Shared.Next(Affirmations.Count)];

    private static ManifestationFixedFallback? BuildFixedContent(ManifestationFixedFallbackOptions? options)
    {
        if (options is null
            || string.IsNullOrWhiteSpace(options.Name)
            || string.IsNullOrWhiteSpace(options.MantraTemplate)
            || string.IsNullOrWhiteSpace(options.Description))
        {
            return null;
        }

        return new ManifestationFixedFallback(
            options.Name.Trim(),
            options.MantraTemplate.Trim(),
            options.Description.Trim());
    }

    private static bool IsBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            _ = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record ManifestationFallback(string PlantName, string Mantra, string PlantDescription);
public sealed record ManifestationFixedFallback(string PlantName, string MantraTemplate, string PlantDescription);
