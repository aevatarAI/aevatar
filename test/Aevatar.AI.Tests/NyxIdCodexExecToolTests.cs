using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodexExecution;
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
    public void Metadata_ExposesSeparatePrivateSshAndManagedTargets()
    {
        var tool = new NyxIdCodexExecTool(CreateDummyClient());

        tool.Name.Should().Be("codex_exec");
        tool.ApprovalMode.Should().Be(ToolApprovalMode.AlwaysRequire);
        tool.RequiresApproval("{}").Should().BeTrue();
        tool.Description.Should().Contain("private NyxID-backed SSH");
        tool.Description.Should().Contain("managed isolated sandbox");
        tool.ParametersSchema.Should().Contain("\"private_ssh\"");
        tool.ParametersSchema.Should().Contain("\"managed_sandbox\"");
        tool.ParametersSchema.Should().Contain("\"empty_git\"");
        tool.ParametersSchema.Should().Contain("\"prompt\"");
        tool.ParametersSchema.Should().NotContain("\"model\"");
        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        schema.RootElement.GetProperty("properties")
            .EnumerateObject()
            .Select(static property => property.Name)
            .Should()
            .Equal("target", "workspace", "prompt", "timeout_secs");
        tool.ParametersSchema.Should()
            .NotContain("\"credential\"")
            .And.NotContain("\"provision\"");
    }

    [Fact]
    public void ApprovalPolicy_RequiresPrivateSshButAllowsManagedSandbox()
    {
        var tool = new NyxIdCodexExecTool(CreateDummyClient());

        tool.ApprovalMode.Should().Be(ToolApprovalMode.AlwaysRequire);
        tool.RequiresApproval("""{"target":{"kind":"private_ssh"}}""").Should().BeTrue();
        tool.RequiresApproval("""{"target":{"kind":"managed_sandbox"}}""").Should().BeFalse();
        tool.RequiresApproval("{}").Should().BeTrue();
        tool.RequiresApproval("""{"target":{"kind":"unknown"}}""").Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_BuildsTypedSshRequest()
    {
        var executor = new RecordingSshExecutor();
        var tool = new NyxIdCodexExecTool(executor, new NyxIdToolOptions());
        const string prompt = "Inspect this safely'; echo $(id)";

        var result = await tool.ExecuteAsync(JsonSerializer.Serialize(new
        {
            target = new
            {
                kind = "private_ssh",
                private_ssh = new { service = "codex-node", principal = "runner" },
            },
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
                target = new
                {
                    kind = "private_ssh",
                    private_ssh = new { service = "codex-node", principal = "runner" },
                },
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
            target = new
            {
                kind = "private_ssh",
                private_ssh = new { service = "codex-node", principal = "runner" },
            },
            prompt = new string('a', 6001),
        }));

        result.Should().Contain("\"error\":\"prompt_too_large\"");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"prompt":"task"}""", "invalid_target")]
    [InlineData("""{"target":{"kind":"private_ssh"},"prompt":"task"}""", "invalid_target")]
    [InlineData("""{"target":{"kind":"managed_sandbox","private_ssh":{"service":"node","principal":"runner"}},"workspace":{"kind":"empty_git"},"prompt":"task"}""", "mixed_target")]
    [InlineData("""{"target":{"kind":"managed_sandbox"},"prompt":"task"}""", "invalid_workspace")]
    [InlineData("""{"target":{"kind":"private_ssh","private_ssh":{"service":"node","principal":"runner"}},"workspace":{"kind":"empty_git"},"prompt":"task"}""", "mixed_target")]
    public async Task ExecuteAsync_RejectsMissingOrMixedTargetFields(string arguments, string error)
    {
        var tool = new NyxIdCodexExecTool(CreateDummyClient());

        var result = await tool.ExecuteAsync(arguments);

        result.Should().Contain($"\"error\":\"{error}\"");
    }

    [Fact]
    public async Task ExecuteAsync_RoutesManagedTargetWithTypedWorkspaceAndNoSshApproval()
    {
        var port = new RecordingManagedPort();
        var tool = new NyxIdCodexExecTool([port], new NyxIdToolOptions());
        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with
        {
            Credentials = AgentToolExecutionContext.Empty.Credentials with
            {
                NyxIdAccessToken = "caller-token",
                NyxIdCredentialKind = AgentToolNyxIdCredentialKind.SourceReadableUserBearer,
            },
        };
        try
        {
            const string arguments = """
                {
                  "target": { "kind": "managed_sandbox" },
                  "workspace": { "kind": "empty_git" },
                  "prompt": "Reply with exactly CODEX_EXEC_READY",
                  "timeout_secs": 180
                }
                """;

            tool.RequiresApproval(arguments).Should().BeFalse();
            var result = await tool.ExecuteAsync(arguments);

            using var resultJson = JsonDocument.Parse(result);
            resultJson.RootElement.GetProperty("status").GetString().Should().Be("succeeded");
            resultJson.RootElement.GetProperty("output").GetString().Should().Be("CODEX_EXEC_READY");
            port.Request.Should().NotBeNull();
            port.Request!.Target.TargetCase.Should().Be(
                CodexExecutionTarget.TargetOneofCase.ManagedSandbox);
            port.Request.Workspace!.WorkspaceCase.Should().Be(
                CodexExecutionWorkspace.WorkspaceOneofCase.EmptyGit);
            port.Request.Caller.NyxIdAccessToken.Should().Be("caller-token");
            port.Request.TimeoutSeconds.Should().Be(180);
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_ManagedTargetUsesSourceReadableBearerInsteadOfProxyDelegation()
    {
        var port = new RecordingManagedPort();
        var tool = new NyxIdCodexExecTool([port], new NyxIdToolOptions());
        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "delegation-alpha",
                "org-alpha",
                "sender-alpha",
                AgentToolNyxIdCredentialKind.ProxyDelegation,
                "source-alpha"),
        };
        try
        {
            await tool.ExecuteAsync("""
                {
                  "target": { "kind": "managed_sandbox" },
                  "workspace": { "kind": "empty_git" },
                  "prompt": "Reply with exactly CODEX_EXEC_READY"
                }
                """);

            port.Request.Should().NotBeNull();
            port.Request!.Caller.NyxIdAccessToken.Should().Be("source-alpha");
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_ManagedTargetDoesNotTreatOrganizationOrSenderCredentialAsSourceReadable()
    {
        var port = new RecordingManagedPort();
        var tool = new NyxIdCodexExecTool([port], new NyxIdToolOptions());
        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "delegation-alpha",
                "source-alpha",
                "source-beta",
                AgentToolNyxIdCredentialKind.ProxyDelegation),
        };
        try
        {
            await tool.ExecuteAsync("""
                {
                  "target": { "kind": "managed_sandbox" },
                  "workspace": { "kind": "empty_git" },
                  "prompt": "Reply with exactly CODEX_EXEC_READY"
                }
                """);

            port.Request.Should().NotBeNull();
            port.Request!.Caller.NyxIdAccessToken.Should().BeNull();
        }
        finally
        {
            AgentToolRequestContext.Current = null;
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenTargetIsNotRegistered_FailsBeforeExecution()
    {
        var tool = new NyxIdCodexExecTool(CreateDummyClient());

        var result = await tool.ExecuteAsync("""
            {
              "target": { "kind": "managed_sandbox" },
              "workspace": { "kind": "empty_git" },
              "prompt": "task"
            }
            """);

        result.Should().Contain("\"error\":\"target_not_configured\"");
    }

    [Fact]
    public async Task ExecuteAsync_WhenManagedPortFails_ThrowsTypedFailureForWorkflowOutcome()
    {
        var tool = new NyxIdCodexExecTool(
            [new FailingManagedPort()],
            new NyxIdToolOptions());

        var act = () => tool.ExecuteAsync("""
            {
              "target": { "kind": "managed_sandbox" },
              "workspace": { "kind": "empty_git" },
              "prompt": "task"
            }
            """);

        var exception = await act.Should().ThrowAsync<CodexExecutionException>();
        exception.Which.Failure.Kind.Should().Be(CodexExecutionFailureKind.ProvisioningFailed);
        exception.Which.Failure.Code.Should().Be("sandbox_provisioning_failed");
    }

    [Fact]
    public void CreateResultReceipt_WhenManagedResultSucceeded_ReportsVerifiedSuccess()
    {
        var tool = new NyxIdCodexExecTool(CreateDummyClient());

        var receipt = tool.CreateResultReceipt(
            "call-1",
            "codex_exec",
            "{}",
            """{"status":"succeeded","target":"managed_sandbox","output":"done","exit_code":0,"diagnostic_id":"diag","elapsed_ms":42}""");

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.CallId.Should().Be("call-1");
        receipt.ToolName.Should().Be("codex_exec");
    }

    [Fact]
    public void CreateResultReceipt_WhenPrivateSshExitedCleanly_ReportsVerifiedSuccess()
    {
        var tool = new NyxIdCodexExecTool(CreateDummyClient());

        var receipt = tool.CreateResultReceipt(
            "call-1",
            "codex_exec",
            "{}",
            """{"exit_code":0,"stdout":"done","stderr":"","duration_ms":42,"timed_out":false}""");

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
    }

    [Theory]
    [InlineData(
        """{"error":"invalid_target","detail":"'target.kind' is required."}""",
        "invalid_target",
        "'target.kind' is required.")]
    [InlineData(
        """{"error":"ssh_timeout","detail":"NyxID did not return an SSH exec response within 45s."}""",
        "ssh_timeout",
        "NyxID did not return an SSH exec response within 45s.")]
    [InlineData(
        """{"error":"No NyxID access token available. User must be authenticated."}""",
        "codex_exec_failed",
        "No NyxID access token available. User must be authenticated.")]
    public void CreateResultReceipt_WhenToolReturnedErrorJson_CarriesStableFailureCode(
        string resultJson,
        string expectedCode,
        string expectedMessage)
    {
        var tool = new NyxIdCodexExecTool(CreateDummyClient());

        var receipt = tool.CreateResultReceipt("call-1", "codex_exec", "{}", resultJson);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be(expectedCode);
        receipt.ErrorMessage.Should().Be(expectedMessage);
        receipt.ResultJson.Should().Contain(expectedCode);
    }

    [Theory]
    [InlineData("""{"exit_code":2,"stdout":"","stderr":"boom","timed_out":false}""", "codex_exec_nonzero_exit")]
    [InlineData("""{"exit_code":0,"stdout":"","stderr":"","timed_out":true}""", "codex_exec_timed_out")]
    public void CreateResultReceipt_WhenPrivateSshFailed_ReportsTypedError(
        string resultJson,
        string expectedCode)
    {
        var tool = new NyxIdCodexExecTool(CreateDummyClient());

        var receipt = tool.CreateResultReceipt("call-1", "codex_exec", "{}", resultJson);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be(expectedCode);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"status":"running"}""")]
    [InlineData("""{"exit_code":0}""")]
    [InlineData("""{"exit_code":0,"timed_out":null}""")]
    [InlineData("""{"exit_code":0,"timed_out":"false"}""")]
    [InlineData("""{"stdout":"no terminal markers"}""")]
    public void CreateResultReceipt_WhenOutcomeIsAmbiguous_StaysUnknown(string resultJson)
    {
        var tool = new NyxIdCodexExecTool(CreateDummyClient());

        tool.CreateResultReceipt("call-1", "codex_exec", "{}", resultJson).Should().BeNull();
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

    private sealed class RecordingManagedPort : ICodexExecutionPort
    {
        public CodexExecutionRequest? Request { get; private set; }

        public CodexExecutionTarget.TargetOneofCase TargetKind =>
            CodexExecutionTarget.TargetOneofCase.ManagedSandbox;

        public async IAsyncEnumerable<CodexExecutionEvent> ExecuteAsync(
            CodexExecutionRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Request = request;
            yield return CodexExecutionEvent.Started();
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return CodexExecutionEvent.Completed(new CodexExecutionResult(
                "CODEX_EXEC_READY",
                ExitCode: 0,
                DiagnosticId: "sandbox-diag",
                ElapsedMilliseconds: 42));
        }
    }

    private sealed class FailingManagedPort : ICodexExecutionPort
    {
        public CodexExecutionTarget.TargetOneofCase TargetKind =>
            CodexExecutionTarget.TargetOneofCase.ManagedSandbox;

        public async IAsyncEnumerable<CodexExecutionEvent> ExecuteAsync(
            CodexExecutionRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return CodexExecutionEvent.Failed(new CodexExecutionFailure(
                CodexExecutionFailureKind.ProvisioningFailed,
                "sandbox_provisioning_failed",
                "provisioning failed"));
        }
    }
}
