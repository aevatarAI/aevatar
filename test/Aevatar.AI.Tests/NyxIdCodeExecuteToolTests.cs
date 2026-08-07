using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodeExecution;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdCodeExecuteToolTests : IDisposable
{
    private static readonly CodeExecutionRouteIdentity ResolvedRoute = new(
        "chrono-sandbox",
        "svc-code-alpha",
        CodeExecutionRouteIdentitySource.NyxIdUserServiceCatalog);

    [Fact]
    public async Task ToolSource_WithoutCodePort_DoesNotExposeCodeExecute()
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var source = new NyxIdAgentToolSource(
            options,
            new NyxIdApiClient(options, new HttpClient()));

        var tools = await source.DiscoverToolsAsync();

        tools.Should().NotContain(tool => tool.Name == "code_execute");
    }

    [Fact]
    public async Task ToolSource_WithDuplicateCodePorts_FailsClosed()
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var outcome = CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute);
        var source = new NyxIdAgentToolSource(
            options,
            new NyxIdApiClient(options, new HttpClient()),
            codeExecutionPorts: [
                new StubCodeExecutionPort(outcome),
                new StubCodeExecutionPort(outcome),
            ]);

        var act = () => source.DiscoverToolsAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exactly one ICodeExecutionPort*");
    }

    [Fact]
    public void Metadata_DescribesExactSourceExecutionWithoutClaimingDeterminism()
    {
        var tool = CreateTool(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));

        tool.Name.Should().Be("code_execute");
        tool.Description.Should().Contain("caller-provided exact source code");
        tool.Description.Should().Contain("one-shot remote code runtime");
        tool.Description.Should().Contain("stdout, stderr, and exit code");
        tool.Description.Should().Contain("use codex_exec to delegate a natural-language task to an agent");
        tool.Description.Should().NotContain("deterministic");
        tool.ApprovalMode.Should().Be(ToolApprovalMode.NeverRequire);
    }

    [Fact]
    public void ReplayContract_OneShotCodeRuntime_IsNonReplayable()
    {
        IAgentTool tool = CreateTool(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        const string arguments = """{"language":"python","code":"print(1 + 1)"}""";

        tool.GetCallSafety(arguments).Should().Be(new AgentToolCallSafety(
            RequiresApproval: null,
            IsReadOnly: true,
            IsDestructive: false));
        tool.ResolveReplayPolicy(arguments).Should().Be(AgentToolReplayPolicy.NonReplayable);
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_UsesTypedRouteAndSourceReadableCredential()
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult("42\n", string.Empty, 0, "diag-code-1", 17),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        SetProxyDelegation("request-delegation", "source-readable-bearer");

        var terminal = await tool.ExecuteWithOutcomeAsync(
            "call-1",
            tool.Name,
            """{"language":"python","code":"print(42)"}""");

        port.Request.Should().Be(new CodeExecutionRequest(
            CodeExecutionLanguage.Python,
            "print(42)",
            new CodeExecutionRouteIdentity(
                "chrono-sandbox",
                null,
                CodeExecutionRouteIdentitySource.CodeExecutionContract),
            new CodeExecutionCallerContext("source-readable-bearer")));
        terminal.Receipt.Should().NotBeNull();
        terminal.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        terminal.Receipt.SubjectId.Should().Be("svc-code-alpha");
        using var document = JsonDocument.Parse(terminal.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("output").GetProperty("stdout").GetString().Should().Be("42\n");
        root.GetProperty("output").GetProperty("exit_code").GetInt32().Should().Be(0);
        root.GetProperty("output").GetProperty("diagnostic_id").GetString().Should().Be("diag-code-1");
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_IgnoresConnectedServicesPresentationText()
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                "source-readable-bearer",
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
            ConnectedServices = new AgentToolConnectedServicesContext(
                "- **Managed** (slug: `presentation-sandbox`)"),
        };

        await tool.ExecuteAsync("""{"language":"bash","code":"printf ok"}""");

        port.Request!.Route.Should().Be(new CodeExecutionRouteIdentity(
            "chrono-sandbox",
            null,
            CodeExecutionRouteIdentitySource.CodeExecutionContract));
    }

    [Theory]
    [InlineData("go")]
    [InlineData("java")]
    [InlineData("Python")]
    public async Task ExecuteWithOutcomeAsync_UnsupportedLanguage_FailsBeforeDispatch(string language)
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        SetSourceReadableBearer("source-readable-bearer");

        var terminal = await tool.ExecuteWithOutcomeAsync(
            "call-unsupported-language",
            tool.Name,
            JsonSerializer.Serialize(new { language, code = "source" }));

        port.Request.Should().BeNull();
        AssertFailure(
            terminal,
            "code_execution_request_invalid",
            "Language must be one of: python, javascript, typescript, bash.");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"language\":\"python\"}")]
    [InlineData("{\"code\":\"print(1)\"}")]
    public async Task ExecuteWithOutcomeAsync_InvalidArguments_ReturnsTypedFailureWithoutDispatch(
        string arguments)
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        SetSourceReadableBearer("source-readable-bearer");

        var terminal = await tool.ExecuteWithOutcomeAsync("call-invalid", tool.Name, arguments);

        port.Request.Should().BeNull();
        AssertFailure(
            terminal,
            "code_execution_request_invalid",
            "Both 'language' and 'code' are required.");
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_WithoutSourceReadableCredential_FailsBeforeDispatch()
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        SetProxyDelegation("request-delegation", sourceReadableBearer: null);

        var terminal = await tool.ExecuteWithOutcomeAsync(
            "call-no-credential",
            tool.Name,
            """{"language":"python","code":"print(1)"}""");

        port.Request.Should().BeNull();
        AssertFailure(
            terminal,
            "code_execution_credential_unavailable",
            "A source-readable NyxID credential is required for code execution.");
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_NonZeroExit_PreservesResultAndFailureReceipt()
    {
        var failure = new CodeExecutionFailure(
            CodeExecutionFailureKind.ExecutionFailed,
            "EXECUTION_FAILED",
            "Code execution exited unsuccessfully.",
            "diag-code-2");
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.CompletedWithFailure(
            new CodeExecutionResult("partial", "traceback", 7, "diag-code-2", 31),
            failure,
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        SetSourceReadableBearer("source-readable-bearer");

        var terminal = await tool.ExecuteWithOutcomeAsync(
            "call-nonzero",
            tool.Name,
            """{"language":"python","code":"raise RuntimeError()"}""");

        terminal.Receipt.Should().NotBeNull();
        terminal.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        terminal.Receipt.ErrorCode.Should().Be("EXECUTION_FAILED");
        terminal.Receipt.SubjectId.Should().Be("svc-code-alpha");
        terminal.Receipt.ResultJson.Should().Be(terminal.ResultJson);
        using var document = JsonDocument.Parse(terminal.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("error").GetString().Should().Be("EXECUTION_FAILED");
        root.GetProperty("code").GetString().Should().Be("EXECUTION_FAILED");
        root.GetProperty("message").GetString().Should().Be(failure.Message);
        root.GetProperty("diagnostic_id").GetString().Should().Be("diag-code-2");
        root.GetProperty("output").GetProperty("stdout").GetString().Should().Be("partial");
        root.GetProperty("output").GetProperty("stderr").GetString().Should().Be("traceback");
        root.GetProperty("output").GetProperty("exit_code").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_TransportFailure_PreservesTypedPublicEnvelope()
    {
        var port = new StubCodeExecutionPort(CodeExecutionOutcome.Failed(
            new CodeExecutionFailure(
                CodeExecutionFailureKind.TimedOut,
                "code_execution_timed_out",
                "Code execution timed out.",
                "diag-code-timeout")));
        var tool = new NyxIdCodeExecuteTool(port);
        SetSourceReadableBearer("source-readable-bearer");

        var terminal = await tool.ExecuteWithOutcomeAsync(
            "call-timeout",
            tool.Name,
            """{"language":"javascript","code":"while (true) {}"}""");

        AssertFailure(terminal, "code_execution_timed_out", "Code execution timed out.");
        using var document = JsonDocument.Parse(terminal.ResultJson);
        document.RootElement.GetProperty("diagnostic_id").GetString()
            .Should().Be("diag-code-timeout");
        document.RootElement.TryGetProperty("output", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteWithOutcomeAsync_ContradictoryPortOutcome_FailsClosed()
    {
        var port = new StubCodeExecutionPort(new CodeExecutionOutcome(
            new CodeExecutionResult("unexpected", string.Empty, 0),
            new CodeExecutionFailure(
                CodeExecutionFailureKind.ExecutionFailed,
                "EXECUTION_FAILED",
                "Code execution exited unsuccessfully."),
            ResolvedRoute));
        var tool = new NyxIdCodeExecuteTool(port);
        SetSourceReadableBearer("source-readable-bearer");

        var terminal = await tool.ExecuteWithOutcomeAsync(
            "call-invalid-outcome",
            tool.Name,
            """{"language":"python","code":"print(1)"}""");

        AssertFailure(
            terminal,
            "code_execution_outcome_invalid",
            "Code execution returned an invalid outcome.");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"success\":true,\"output\":{\"stdout\":\"ok\",\"stderr\":\"\",\"exit_code\":1}}")]
    [InlineData("{\"success\":false,\"error\":\"EXECUTION_FAILED\",\"code\":\"OTHER\",\"message\":\"failed\"}")]
    [InlineData("{\"success\":false,\"error\":\"EXECUTION_FAILED\",\"code\":\"EXECUTION_FAILED\",\"message\":\"failed\",\"output\":{\"stdout\":\"\",\"stderr\":\"\",\"exit_code\":0}}")]
    public void CreateResultReceipt_ContradictoryEnvelope_RemainsUnverified(string resultJson)
    {
        IAgentTool tool = CreateTool(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));

        tool.CreateResultReceipt("call-unverified", tool.Name, "{}", resultJson).Should().BeNull();
    }

    [Theory]
    [InlineData("EXECUTION_FAILED")]
    [InlineData("DEPENDENCY_INSTALL_FAILED")]
    public void CreateResultReceipt_WhenCompletedFailureOmitsOutput_RemainsUnverified(string code)
    {
        IAgentTool tool = CreateTool(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        var resultJson = JsonSerializer.Serialize(new
        {
            success = false,
            error = code,
            code,
            message = "failed",
        });

        tool.CreateResultReceipt("call-missing-output", tool.Name, "{}", resultJson)
            .Should().BeNull();
    }

    [Theory]
    [InlineData("UNAUTHENTICATED")]
    [InlineData("FORBIDDEN")]
    public void CreateResultReceipt_WhenChronoAuthorizationFailureIsTyped_PreservesReceipt(string code)
    {
        IAgentTool tool = CreateTool(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));
        var resultJson = JsonSerializer.Serialize(new
        {
            success = false,
            error = code,
            code,
            message = "Code execution authorization failed upstream.",
        });

        var receipt = tool.CreateResultReceipt("call-auth-failure", tool.Name, "{}", resultJson);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        receipt.ErrorCode.Should().Be(code);
        receipt.ResultJson.Should().Be(resultJson);
    }

    [Fact]
    public void CreateResultReceipt_WhenFailureCodeIsNotOwned_RemainsUnverified()
    {
        IAgentTool tool = CreateTool(CodeExecutionOutcome.Succeeded(
            new CodeExecutionResult(string.Empty, string.Empty, 0),
            ResolvedRoute));

        tool.CreateResultReceipt(
                "call-unknown-code",
                tool.Name,
                "{}",
                """{"success":false,"error":"UNKNOWN_PROVIDER_CODE","code":"UNKNOWN_PROVIDER_CODE","message":"failed"}""")
            .Should().BeNull();
    }

    public void Dispose()
    {
        AgentToolRequestContext.Current = null;
        GC.SuppressFinalize(this);
    }

    private static NyxIdCodeExecuteTool CreateTool(CodeExecutionOutcome outcome) =>
        new(new StubCodeExecutionPort(outcome));

    private static void AssertFailure(
        AgentToolTerminalOutcome terminal,
        string code,
        string message)
    {
        terminal.Receipt.Should().NotBeNull();
        terminal.Receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
        terminal.Receipt.ErrorCode.Should().Be(code);
        terminal.Receipt.ErrorMessage.Should().Be(message);
        terminal.Receipt.ResultJson.Should().Be(terminal.ResultJson);
        using var document = JsonDocument.Parse(terminal.ResultJson);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeFalse();
        root.GetProperty("error").GetString().Should().Be(code);
        root.GetProperty("code").GetString().Should().Be(code);
        root.GetProperty("message").GetString().Should().Be(message);
    }

    private static void SetSourceReadableBearer(string bearer)
    {
        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                bearer,
                null,
                null,
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer),
        };
    }

    private static void SetProxyDelegation(string delegation, string? sourceReadableBearer)
    {
        AgentToolRequestContext.Current = AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(
                delegation,
                null,
                null,
                AgentToolNyxIdCredentialKind.ProxyDelegation,
                sourceReadableBearer),
        };
    }

    private sealed class StubCodeExecutionPort(CodeExecutionOutcome outcome) : ICodeExecutionPort
    {
        public CodeExecutionRequest? Request { get; private set; }

        public Task<CodeExecutionOutcome> ExecuteAsync(
            CodeExecutionRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(outcome);
        }
    }
}
