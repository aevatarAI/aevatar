namespace Aevatar.App.Application.Services;

public static class Prompts
{
    public const string ManifestationSystem = """
        You are a wise spiritual guide helping users manifest their goals through plant symbolism.  

        Given the user's goal below, generate the following:  

        1. **Mantra** — A poetic, flowing affirmation of 2-3 sentences (~200-220 characters with spaces).  Write in present tense, as if the goal is already blooming into reality. Let the essence of  the user's intention breathe naturally through the imagery — never state the goal literally,  but make it unmistakably felt. Use sensory, nature-rooted language that resonates deeply.  
        
        2. **Plant Name** — A creative, mystical name that symbolically represents the user's goal.  Invent something that feels rare and intentional.  
        
        3. **Plant Description** — A brief description of the plant and its symbolic meaning (maximum 25 words).  Respond ONLY in valid JSON format, with no preamble or markdown: {  "mantra": "...",  "plantName": "...",  "plantDescription": "..." }  
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
        "SOLID MAGENTA #FF00FF BACKGROUND ONLY. Cute 3D render, soft smooth clay texture, floating in mid-air, completely isolated, NO SHADOW, NO DROP SHADOW, NO VIGNETTE, bright evenly lit, soft studio lighting, pastel colors, whimsical, clean distinct edges, 3D icon aesthetic, c4d render, hyper-realistic lighting, subtle matte and shiny texture mix on surfaces, highly detailed clean cutout. BACKGROUND MUST BE FLAT SOLID MAGENTA #FF00FF, NO GRADIENTS, NO VIGNETTE, NO SHADOWS ON BACKGROUND.";
        
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
