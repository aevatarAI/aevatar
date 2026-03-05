namespace Aevatar.App.Application.Services;

public static class Prompts
{
    public const string ManifestationSystem = """
        You are a wise spiritual guide helping users manifest their goals through plant symbolism.

        Given the user's goal, generate:
        1. A short, powerful mantra (1-2 sentences) that they can repeat while nurturing their virtual plant
        2. A unique plant name that symbolically represents their goal (be creative and mystical)
        3. A brief description of the plant and its symbolic meaning (maximum 25 words, keep it concise)

        Respond ONLY in valid JSON format:
        {
          "mantra": "...",
          "plantName": "...",
          "plantDescription": "..."
        }
        """;

    public static string ManifestationUser(string userGoal) => $"User's goal: {userGoal}";

    public static string Affirmation(string mantra, string plantName, string userGoal) =>
        $"""
        Generate a short, encouraging 1-sentence daily affirmation for someone manifesting "{mantra}" with the clear goal of "{userGoal}".
        Metaphorically reference watering their "{plantName}".
        Keep it concise and elegant.
        Respond with plain text only. Do not use any markdown syntax, formatting, or special characters like asterisks, hashes, or backticks.
        """;

    public const string ImageStyle =
        "cute 3D render, soft smooth clay texture, floating in mid-air, completely isolated on a FLAT SOLID PURE WHITE background (#FFFFFF), " +
        "NO SHADOW, NO DROP SHADOW, NO CAST SHADOW, NO GRADIENT, NO VIGNETTE, NO FLOOR, NO SURFACE, NO TABLE, NO GROUND, NO REFLECTION, " +
        "bright evenly lit, soft studio lighting, pastel colors, whimsical, clean sharp distinct edges with NO anti-aliasing blur, " +
        "no white halo at edges, 3d icon aesthetic, c4d render, do NOT render any text or letters or words or labels in the image";

    public static string PlantImage(string plantName, string plantDescription, string stage) => stage switch
    {
        "seed" => $"A cute, single magical seed of a {plantName} floating. 3D clay render, minimalist, adorable, glowing details, no shadow. {ImageStyle}",
        "sprout" => $"A tiny, adorable sprout of a {plantName} floating. 3D clay render, soft, friendly, new life, magical energy, no shadow. {ImageStyle}",
        "growing" => $"A happy, growing magical plant ({plantName}) floating in mid-air. 3D clay render, vibrant, healthy, cute, magical leaves, no shadow. {plantDescription}. {ImageStyle}",
        "blooming" => $"A magnificent, fully bloomed {plantName} flower floating in the air, magical aura, bioluminescence. 3D clay render, breathtaking, centerpiece, no shadow. {plantDescription}. {ImageStyle}",
        _ => $"A magical {plantName} plant. {ImageStyle}",
    };

    public static string Speech(string text) =>
        $"""Speak this affirmation in a soothing, calm, and warm way: "{text}" """;

    public static string ToGeminiImagePayload(string prompt) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { responseModalities = new[] { "TEXT", "IMAGE" } },
        });

    public static string ToGeminiSpeechPayload(string prompt) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new
            {
                responseModalities = new[] { "AUDIO" },
                speechConfig = new
                {
                    voiceConfig = new
                    {
                        prebuiltVoiceConfig = new { voiceName = "Kore" },
                    },
                },
            },
        });
}
