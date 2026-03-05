namespace Aevatar.App.Application.Services;

public sealed class GenerationAppService : IGenerationAppService
{
    private readonly IAIGenerationAppService _ai;
    private readonly IFallbackContent _fallbackContent;

    public GenerationAppService(
        IAIGenerationAppService ai,
        IFallbackContent fallbackContent)
    {
        _ai = ai;
        _fallbackContent = fallbackContent;
    }

    public async Task<ManifestationResult> GenerateManifestationAsync(string userGoal, CancellationToken ct)
    {
        try
        {
            return await _ai.GenerateContentAsync(userGoal, ct);
        }
        catch
        {
            var fb = _fallbackContent.GetManifestationFixedFallback(userGoal);
            return new ManifestationResult(fb.Mantra, fb.PlantName, fb.PlantDescription);
        }
    }

    public async Task<AffirmationResult> GenerateAffirmationAsync(
        string userGoal,
        string mantra,
        string plantName,
        CancellationToken ct)
    {
        try
        {
            return await _ai.GenerateAffirmationAsync(userGoal, mantra, plantName, ct);
        }
        catch
        {
            return new AffirmationResult(_fallbackContent.FixedAffirmation);
        }
    }
}
