namespace Aevatar.Tools.MockNyxId;

/// <summary>
/// Configurable behavior for the mock NyxID server.
/// Set via appsettings.json section "MockNyxId" or environment variables.
/// </summary>
public sealed class MockNyxIdOptions
{
    public string DefaultUserId { get; set; } = "user-123";
    public string DefaultUserEmail { get; set; } = "test@example.com";
    public string DefaultUserName { get; set; } = "Test User";

    /// <summary>Text content the mock LLM gateway returns.</summary>
    public string LlmResponseText { get; set; } = "This is a mock response from MockNyxId.";

    /// <summary>Model name echoed in LLM responses.</summary>
    public string LlmModel { get; set; } = "mock-gpt-4";

    /// <summary>Artificial delay in ms for LLM responses (0 = instant).</summary>
    public int LlmResponseDelayMs { get; set; }

    /// <summary>JWT signing key for test tokens.</summary>
    public string JwtSigningKey { get; set; } = "mock-nyxid-test-signing-key-at-least-32-bytes!";

    /// <summary>Port for standalone mode.</summary>
    public int Port { get; set; } = 5199;
}
