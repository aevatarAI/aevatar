namespace Aevatar.App.Application.Services;

public interface IAIGenerationAppService
{
    Task<ManifestationResult> GenerateContentAsync(string userGoal, CancellationToken ct = default);

    Task<AffirmationResult> GenerateAffirmationAsync(
        string userGoal, string mantra, string plantName, CancellationToken ct = default);

    Task<ImageResult> GenerateImageAsync(
        string plantName, string plantDescription, string stage, CancellationToken ct = default);

    Task<SpeechResult> GenerateSpeechAsync(string text, CancellationToken ct = default);
}
