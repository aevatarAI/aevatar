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

    public const string ImageWithBgStyle =
        "SOLID MAGENTA #FF00FF BACKGROUND ONLY. Cute 3D render, soft smooth clay texture, floating in mid-air, completely isolated, NO SHADOW, NO DROP SHADOW, NO VIGNETTE, bright evenly lit, soft studio lighting, pastel colors, whimsical, clean distinct edges, 3D icon aesthetic, c4d render, hyper-realistic lighting, subtle matte and shiny texture mix on surfaces, highly detailed clean cutout. BACKGROUND MUST BE FLAT SOLID MAGENTA #FF00FF, NO GRADIENTS, NO VIGNETTE, NO SHADOWS ON BACKGROUND.";

    public const string ImageStyle =
        "Cute 3D render, soft smooth clay texture, floating in mid-air, completely isolated on the provided green background. ABSOLUTELY NO SHADOW, NO DROP SHADOW, NO CAST SHADOW, NO AMBIENT OCCLUSION SHADOW, NO VIGNETTE, NO GRADIENT, NO DARKENING AT EDGES OR CORNERS. The green background must remain perfectly uniform and unaltered. CRITICAL COLOR RULE: The subject must NEVER use bright green, lime green, neon green, or any vivid saturated green similar to the background (#33CC33). Allowed greens for the subject: only dark forest green, olive green, sage green, muted dusty green, or desaturated yellow-green. Prefer using pinks, purples, blues, corals, lavenders, golds, and warm pastels for most of the subject. Bright evenly lit, soft studio lighting, pastel colors, whimsical, clean distinct edges, 3D icon aesthetic, c4d render, hyper-realistic lighting, subtle matte and shiny texture mix on surfaces, highly detailed clean cutout. Place the subject centered and floating in front of the provided background. Do not alter the background in any way.";

    public const string GreenBgBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAgAAAAIACAIAAAB7GkOtAAAHH0lEQVR4nO3VMQ0AMAzAsHIqfwYDNRg9YskA8mX2LQBBc14AwAkDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgCgDAIgyAIAoAwCIMgCAKAMAiDIAgKgP9wRHueGh42UAAAAASUVORK5CYII=";

    public static string PlantImage(string plantName, string plantDescription, string stage, bool useInlineData = false)
    {
        var style = useInlineData ? ImageStyle : ImageWithBgStyle;

        return stage switch
        {
            "seed" => $"A cute, single magical seed of a {plantName} floating. 3D clay render, minimalist, adorable, glowing details, no shadow. {style}",
            "sprout" => $"A tiny, adorable sprout of a {plantName} floating. 3D clay render, soft, friendly, new life, magical energy, no shadow. {style}",
            "growing" => $"A happy, growing magical plant ({plantName}) floating in mid-air. 3D clay render, vibrant, healthy, cute, magical leaves, no shadow. {plantDescription}. {style}",
            "blooming" => $"A magnificent, fully bloomed {plantName} flower floating in the air, magical aura, bioluminescence. 3D clay render, breathtaking, centerpiece, no shadow. {plantDescription}. {style}",
            _ => $"A magical {plantName} plant. {style}",
        };
    }

    public static string Speech(string text) =>
        $"""Speak this affirmation in a soothing, calm, and warm way: "{text}" """;

    public static string ToGeminiImagePayload(string prompt, string? inlineImageBase64 = null)
        => string.IsNullOrWhiteSpace(inlineImageBase64)
            ? System.Text.Json.JsonSerializer.Serialize(new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } },
                generationConfig = new { responseModalities = new[] { "TEXT", "IMAGE" } },
            })
            : System.Text.Json.JsonSerializer.Serialize(new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = prompt },
                            new
                            {
                                inline_data = new
                                {
                                    mime_type = "image/png",
                                    data = inlineImageBase64,
                                },
                            },
                        },
                    },
                },
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
