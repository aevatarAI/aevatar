using System.Net.Http.Headers;
using System.Text;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.LLMProviders.NyxId;
using FluentAssertions;
using OpenAI;

namespace Aevatar.Bootstrap.Tests;

/// <summary>
/// Diagnostic tests to reproduce and investigate the NyxID LLM gateway 403 issue.
/// Run with: dotnet test test/Aevatar.Bootstrap.Tests --filter "NyxIdGateway403" --nologo
/// Set NYXID_TEST_TOKEN env var to the access token from browser localStorage.
/// </summary>
[Trait("Category", "Integration")]
public class NyxIdGateway403DiagnosticTests
{
    private const string GatewayBase = "https://nyx-api.chrono-ai.fun/api/v1/llm/gateway/v1";
    private const string Model = "deepseek-chat";

    private static string? GetToken() =>
        Environment.GetEnvironmentVariable("NYXID_TEST_TOKEN")?.Trim();

    [Fact]
    public async Task RawHttpClient_ShouldSucceed()
    {
        var token = GetToken();
        if (string.IsNullOrWhiteSpace(token))
            return;

        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{GatewayBase}/chat/completions")
        {
            Content = new StringContent(
                """{"model":"deepseek-chat","messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Console.Error.WriteLine("=== Raw HttpClient request ===");
        Console.Error.WriteLine($"URL: {request.RequestUri}");
        foreach (var h in request.Headers)
            Console.Error.WriteLine($"  {h.Key}: {string.Join(", ", h.Value)}");

        var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Console.Error.WriteLine($"Status: {(int)response.StatusCode}");
        Console.Error.WriteLine($"Body: {body[..Math.Min(body.Length, 500)]}");

        ((int)response.StatusCode).Should().Be(200, $"gateway returned: {body}");
    }

    [Fact]
    public async Task OpenAISdk_ShouldSucceed()
    {
        var token = GetToken();
        if (string.IsNullOrWhiteSpace(token))
            return;

        var endpoint = new Uri(GatewayBase.TrimEnd('/') + "/");
        var options = new OpenAIClientOptions { Endpoint = endpoint };

        var diag = new DiagnosticPolicy();
        options.AddPolicy(diag, System.ClientModel.Primitives.PipelinePosition.BeforeTransport);

        var client = new OpenAIClient(new System.ClientModel.ApiKeyCredential(token), options);
        var chatClient = client.GetChatClient(Model);

        try
        {
            var result = await chatClient.CompleteChatAsync("hi");
            result.Value.Content.Should().NotBeEmpty();
        }
        catch (Exception ex)
        {
            var info = diag.GetDiagnosticInfo();
            throw new Exception($"OpenAI SDK 403 diagnostic:\n{info}", ex);
        }
    }

    [Fact]
    public async Task NyxIdLLMProvider_ShouldSucceed()
    {
        var token = GetToken();
        if (string.IsNullOrWhiteSpace(token))
            return;

        var provider = new NyxIdLLMProvider(
            "nyxid-test",
            Model,
            GatewayBase,
            static () => null);

        var request = new LLMRequest
        {
            Messages = [ChatMessage.User("hi")],
            Model = Model,
            Metadata = new Dictionary<string, string>
            {
                [LLMRequestMetadataKeys.NyxIdAccessToken] = token,
            },
        };

        Console.Error.WriteLine("=== NyxIdLLMProvider request ===");
        try
        {
            var response = await provider.ChatAsync(request);
            Console.Error.WriteLine($"Success! Response: {response.Content?[..Math.Min(response.Content.Length, 200)]}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private sealed class DiagnosticPolicy : System.ClientModel.Primitives.PipelinePolicy
    {
        private readonly StringBuilder _log = new();

        public string GetDiagnosticInfo() => _log.ToString();

        public override void Process(
            System.ClientModel.Primitives.PipelineMessage message,
            IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline,
            int currentIndex)
        {
            CaptureRequest(message);
            ProcessNext(message, pipeline, currentIndex);
            CaptureResponse(message);
        }

        public override async ValueTask ProcessAsync(
            System.ClientModel.Primitives.PipelineMessage message,
            IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline,
            int currentIndex)
        {
            CaptureRequest(message);
            await ProcessNextAsync(message, pipeline, currentIndex);
            CaptureResponse(message);
        }

        private void CaptureRequest(System.ClientModel.Primitives.PipelineMessage message)
        {
            _log.AppendLine($"REQUEST: {message.Request.Method} {message.Request.Uri}");
            foreach (var h in message.Request.Headers)
            {
                var val = h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    ? h.Value?[..Math.Min(h.Value.Length, 40)] + "..."
                    : h.Value;
                _log.AppendLine($"  {h.Key}: {val}");
            }
        }

        private void CaptureResponse(System.ClientModel.Primitives.PipelineMessage message)
        {
            _log.AppendLine($"RESPONSE: {message.Response?.Status}");
            if (message.Response?.Status is >= 400)
            {
                try
                {
                    using var reader = new StreamReader(message.Response.ContentStream!, leaveOpen: true);
                    var body = reader.ReadToEnd();
                    message.Response.ContentStream!.Position = 0;
                    _log.AppendLine($"RESPONSE BODY: {body}");
                }
                catch { }
            }
        }
    }
}
