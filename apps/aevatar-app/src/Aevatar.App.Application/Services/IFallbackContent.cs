namespace Aevatar.App.Application.Services;

public interface IFallbackContent
{
    IReadOnlyList<ManifestationFallback> Plants { get; }
    IReadOnlyList<string> Affirmations { get; }
    string FixedAffirmation { get; }
    string PlaceholderImage { get; }

    ManifestationFallback GetManifestationFallback();
    ManifestationFallback GetManifestationFixedFallback(string userGoal);
    string GetAffirmationFallback();
}
