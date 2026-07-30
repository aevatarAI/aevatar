using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdCodexExecToolTests
{
    private const string CatalogId = "3e64c683-4289-427e-b599-f7eaf6c01fb1";

    [Fact]
    public void Metadata_UsesNodeLocalCodexDefaultsAndSshApprovalPolicy()
    {
        var tool = new NyxIdCodexExecTool(CreateDummyClient());

        tool.Name.Should().Be("codex_exec");
        tool.ApprovalMode.Should().Be(ToolApprovalMode.AlwaysRequire);
        tool.IsDestructive.Should().BeTrue();
        tool.Description.Should().Contain("codex exec -");
        tool.Description.Should().Contain("local configuration");
        tool.ParametersSchema.Should().Contain("\"service\"");
        tool.ParametersSchema.Should().Contain("\"principal\"");
        tool.ParametersSchema.Should().Contain("\"prompt\"");
        tool.ParametersSchema.Should().NotContain("\"model\"");
    }

    [Fact]
    public void ApprovalPolicy_AlwaysRequiresDurableGrant()
    {
        var tool = new NyxIdCodexExecTool(CreateDummyClient());

        tool.ApprovalMode.Should().Be(ToolApprovalMode.AlwaysRequire);
        tool.IsDestructive.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_BuildsTypedSshRequest()
    {
        var executor = new RecordingSshExecutor();
        var tool = new NyxIdCodexExecTool(executor, new NyxIdToolOptions());
        const string prompt = "Inspect this safely'; echo $(id)";

        var result = await tool.ExecuteAsync(JsonSerializer.Serialize(new
        {
            service = "codex-node",
            principal = "runner",
            prompt,
            timeout_secs = 90,
        }));

        result.Should().Be("""{"exit_code":0}""");
        executor.Request.Should().NotBeNull();
        executor.Request!.Service.Should().Be("codex-node");
        executor.Request.Principal.Should().Be("runner");
        executor.Request.TimeoutSecs.Should().Be(90);
        executor.Request.Command.Should().EndWith("| codex exec -");
        executor.Request.Command.Should().NotContain(prompt);
        DecodePrompt(executor.Request.Command).Should().Be(prompt);
    }

    [Fact]
    public async Task ExecuteAsync_EncodesPromptAndDelegatesToNyxIdSshService()
    {
        var handler = new RecordingHandler();
        var tool = new NyxIdCodexExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        const string prompt = "Fix the test'; touch /tmp/injected; echo '$HOME $(id)\n保留这些字符";
        SetToken("test-token");
        try
        {
            var result = await tool.ExecuteAsync(JsonSerializer.Serialize(new
            {
                service = "codex-node",
                principal = "runner",
                prompt,
                timeout_secs = 120,
            }));

            result.Should().Contain("\"exit_code\":0");
            var exec = handler.Requests.Should().ContainSingle(request =>
                request.Method == HttpMethod.Post &&
                request.Path == $"/api/v1/ssh/{CatalogId}/exec").Subject;
            exec.Authorization.Should().Be("Bearer test-token");

            using var body = JsonDocument.Parse(exec.Body!);
            body.RootElement.GetProperty("principal").GetString().Should().Be("runner");
            body.RootElement.GetProperty("timeout_secs").GetInt32().Should().Be(120);
            var command = body.RootElement.GetProperty("command").GetString()!;
            command.Should().EndWith("| codex exec -");
            command.Should().NotContain(prompt);
            command.Should().NotContain("--model");
            command.Should().NotContain("--sandbox");
            command.Should().NotContain("--dangerously");
            DecodePrompt(command).Should().Be(prompt);
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_RejectsPromptAboveNyxIdCommandBudget()
    {
        var handler = new RecordingHandler();
        var tool = new NyxIdCodexExecTool(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));

        var result = await tool.ExecuteAsync(JsonSerializer.Serialize(new
        {
            service = "codex-node",
            principal = "runner",
            prompt = new string('a', 6001),
        }));

        result.Should().Contain("\"error\":\"prompt_too_large\"");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"principal":"runner","prompt":"task"}""")]
    [InlineData("""{"service":"codex-node","prompt":"task"}""")]
    [InlineData("""{"service":"codex-node","principal":"runner"}""")]
    public async Task ExecuteAsync_RequiresServicePrincipalAndPrompt(string arguments)
    {
        var tool = new NyxIdCodexExecTool(CreateDummyClient());

        var result = await tool.ExecuteAsync(arguments);

        result.Should().Contain("'service', 'principal', and 'prompt' are required");
    }

    private static string DecodePrompt(string command)
    {
        const string prefix = "p='";
        var start = command.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var end = command.IndexOf("';", start, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(prefix.Length);
        end.Should().BeGreaterThan(start);
        return Encoding.UTF8.GetString(Convert.FromBase64String(command[start..end]));
    }

    private static NyxIdApiClient CreateDummyClient() =>
        new(new NyxIdToolOptions { BaseUrl = "https://nyx.example" });

    private static void SetToken(string token)
    {
        AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = token,
        });
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string? Body,
        string? Authorization);

    private sealed class RecordingSshExecutor : INyxIdSshCommandExecutor
    {
        public NyxIdSshCommandRequest? Request { get; private set; }

        public Task<string> ExecuteAsync(
            NyxIdSshCommandRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult("""{"exit_code":0}""");
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                body,
                request.Headers.Authorization?.ToString()));

            var responseBody = request.Method == HttpMethod.Get
                ? $$"""{"id":"user-service","slug":"codex-node","catalog_service_id":"{{CatalogId}}"}"""
                : """{"exit_code":0,"stdout":"done","stderr":"","duration_ms":42,"timed_out":false}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
