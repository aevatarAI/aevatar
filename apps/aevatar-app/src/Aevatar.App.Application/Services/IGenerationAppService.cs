namespace Aevatar.App.Application.Services;

public interface IGenerationAppService
{
    Task<ManifestationResult> GenerateManifestationAsync(string userGoal, CancellationToken ct);

    Task<AffirmationResult> GenerateAffirmationAsync(
        string userGoal, string mantra, string plantName, CancellationToken ct);
}
