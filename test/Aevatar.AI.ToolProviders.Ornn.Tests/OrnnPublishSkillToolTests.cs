using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnPublishSkillToolTests
{
    [Fact]
    public void ToolMetadata_ShouldExposePrivatePublishToolWithAutoApproval()
    {
        var tool = CreateTool(new CapturingHandler("""{ "data": { "valid": true } }"""));

        tool.Name.Should().Be("ornn_publish_skill");
        tool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);
        tool.SideEffectKind.Should().Be("ornn.publish.skill");
        tool.Description.Should().Contain("templates/import sources");
        tool.Description.Should().Contain("not Scope Workflow runtime publication");
        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        var root = schema.RootElement;
        root.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        var properties = root.GetProperty("properties").EnumerateObject().Select(x => x.Name).ToArray();
        properties.Should().Equal(
            "name",
            "description",
            "version",
            "category",
            "instructions_markdown",
            "visibility",
            "tags",
            "output_type",
            "runtimes",
            "runtime_dependencies",
            "runtime_env_vars",
            "tool_list",
            "workflow_yamls",
            "scripts",
            "references",
            "assets");
        properties.Should().NotContain(["license", "compatibility", "metadata", "skill_md", "runtime"]);
    }

    [Theory]
    [InlineData("license")]
    [InlineData("compatibility")]
    [InlineData("metadata")]
    [InlineData("skill_md")]
    [InlineData("runtime")]
    [InlineData("credential")]
    [InlineData("token")]
    [InlineData("owner")]
    [InlineData("user")]
    [InlineData("service_url")]
    [InlineData("guid")]
    [InlineData("skip_validation")]
    [InlineData("allowed_tools")]
    [InlineData("disable_model_invocation")]
    [InlineData("argument_hint")]
    public async Task ExecuteAsync_ShouldRejectUnknownRootFieldsBeforeUpload(string field)
    {
        var handler = new CapturingHandler("""{ "data": { "valid": true } }""");
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ValidArguments($$"""
            "{{field}}": "bad"
            """));

        result.Should().Contain("validation_error");
        result.Should().Contain("unknown_field");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectChildFileNameAliasBeforeUpload()
    {
        var handler = new CapturingHandler("""{ "data": { "valid": true } }""");
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ValidArguments("""
            "scripts": [{ "file_name": "main.cs", "content": "class C {}" }]
            """));

        result.Should().Contain("validation_error");
        result.Should().Contain("unknown_field");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectMissingCallerToken()
    {
        var tool = CreateTool(new CapturingHandler("""{ "data": { "valid": true } }"""));
        AgentToolRequestContext.Current = null;

        var result = await tool.ExecuteAsync(ValidArguments());

        result.Should().Contain("No NyxID access token");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectNonPrivateVisibility()
    {
        var handler = new CapturingHandler("""{ "data": { "valid": true } }""");
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ValidArguments("""
            "visibility": "public"
            """));

        result.Should().Contain("invalid_visibility");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""
        "name": "InvalidName"
        """, "invalid_name")]
    [InlineData("""
        "version": "1.0.0"
        """, "invalid_version")]
    [InlineData("""
        "instructions_markdown": "---\nname: bad\n---\nDo the work."
        """, "invalid_instructions")]
    [InlineData("""
        "tags": ["BadTag"]
        """, "invalid_string")]
    [InlineData("""
        "runtime_env_vars": ["lower_case"]
        """, "invalid_string")]
    [InlineData("""
        "category": "runtime-based",
        "runtimes": ["dotnet"],
        "output_type": "binary"
        """, "invalid_output_type")]
    public async Task ExecuteAsync_ShouldRejectInvalidParserFieldsBeforeUpload(
        string replacementFields,
        string expectedDiagnostic)
    {
        var handler = new CapturingHandler("""{ "data": { "valid": true } }""");
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ArgumentsWith(replacementFields));

        result.Should().Contain("validation_error");
        result.Should().Contain(expectedDiagnostic);
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""
        "category": "plain",
        "tool_list": ["search"]
        """, "invalid_category_fields")]
    [InlineData("""
        "category": "plain",
        "runtimes": ["dotnet"],
        "output_type": "text"
        """, "invalid_category_fields")]
    [InlineData("""
        "category": "tool-based"
        """, "missing_tool_list")]
    [InlineData("""
        "category": "tool-based",
        "tool_list": ["search"],
        "runtimes": ["dotnet"]
        """, "invalid_category_fields")]
    [InlineData("""
        "category": "runtime-based",
        "output_type": "text"
        """, "missing_runtimes")]
    [InlineData("""
        "category": "runtime-based",
        "runtimes": ["dotnet"]
        """, "missing_output_type")]
    [InlineData("""
        "category": "runtime-based",
        "runtimes": ["dotnet"],
        "output_type": "text",
        "tool_list": ["search"]
        """, "invalid_category_fields")]
    [InlineData("""
        "category": "mixed",
        "runtimes": ["dotnet"],
        "output_type": "text"
        """, "missing_tool_list")]
    [InlineData("""
        "category": "mixed",
        "tool_list": ["search"],
        "output_type": "text"
        """, "missing_runtimes")]
    [InlineData("""
        "category": "mixed",
        "runtimes": ["dotnet"],
        "tool_list": ["search"]
        """, "missing_output_type")]
    public async Task ExecuteAsync_ShouldRejectInvalidCategoryMatrixBeforeUpload(
        string replacementFields,
        string expectedDiagnostic)
    {
        var handler = new CapturingHandler("""{ "data": { "valid": true } }""");
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ArgumentsWith(replacementFields));

        result.Should().Contain("validation_error");
        result.Should().Contain(expectedDiagnostic);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSkipUploadWhenLocalValidationFails()
    {
        var handler = new CapturingHandler("""{ "data": { "valid": true } }""");
        var tool = CreateTool(handler, validators:
        [
            new StubValidator(
                OrnnSkillPublishValidationPipeline.WorkflowYamlAssetKind,
                new OrnnSkillPublishDiagnostic("bad_workflow", "bad workflow"))
        ]);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ValidArguments("""
            "workflow_yamls": [{ "workflow_id": "flow", "content": "name: flow\nsteps: []" }]
            """));

        result.Should().Contain("bad_workflow");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSkipPublishWhenOrnnFormatValidationFails()
    {
        var handler = new CapturingHandler("""
            { "data": { "valid": false, "violations": [{ "rule": "skill-md", "message": "bad" }] } }
            """);
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ValidArguments());

        result.Should().Contain("format_validation_error");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldValidateThenPublishOnceOnSuccess()
    {
        var handler = new CapturingHandler(
            """{ "data": { "valid": true, "violations": [] } }""",
            """{ "data": { "guid": "skill-1", "version": "1.1", "skillHash": "hash-1" } }""");
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ValidArguments());

        result.Should().Contain("success");
        result.Should().Contain("skill-1");
        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("guid").GetString().Should().Be("skill-1");
        root.GetProperty("version").GetString().Should().Be("1.1");
        root.GetProperty("skillHash").GetString().Should().Be("hash-1");
        handler.Requests.Select(x => x.RequestUri!.AbsolutePath).Should().Equal(
            "/api/v1/proxy/s/ornn/api/v1/skill-format/validate",
            "/api/v1/proxy/s/ornn/api/v1/skills");
    }

    [Fact]
    public async Task ExecuteAsync_WhenPublishResponseOmitsVersion_ShouldUseRequestedVersionForReceiptSubject()
    {
        var handler = new CapturingHandler(
            """{ "data": { "valid": true, "violations": [] } }""",
            """{ "data": { "id": "skill-2", "hash": "hash-2" } }""");
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ValidArguments());

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("guid").GetString().Should().Be("skill-2");
        root.GetProperty("version").GetString().Should().Be("1.0");
        root.GetProperty("skillHash").GetString().Should().Be("hash-2");
    }

    [Fact]
    public async Task ExecuteAsync_WhenPublishReturnsPermissionError_ShouldSurfaceUpstreamReason()
    {
        var handler = new CapturingHandler(
            new CapturingResponse("""{ "data": { "valid": true, "violations": [] } }"""),
            new CapturingResponse(
                """{ "status": 403, "code": "permission_denied", "detail": "Missing ornn:skill:create permission" }""",
                HttpStatusCode.Forbidden));
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ValidArguments());

        result.Should().Contain("\"status\":\"error\"");
        result.Should().Contain("Missing ornn:skill:create permission");
        result.Should().NotContain("missing proxy scope");
    }

    [Fact]
    public async Task CreateSuccessReceipt_ShouldMapPublishedSkillSubjectFromToolResult()
    {
        var handler = new CapturingHandler(
            """{ "data": { "valid": true, "violations": [] } }""",
            """{ "data": { "id": "skill-3", "hash": "hash-3" } }""");
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ValidArguments());
        var receipt = tool.CreateSuccessReceipt("call-1", tool.Name, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.SideEffectKind.Should().Be("ornn.publish.skill");
        receipt.SubjectKind.Should().Be("ornn.skill");
        receipt.SubjectId.Should().Be("skill-3");
        receipt.SubjectVersion.Should().Be("1.0");
        receipt.SubjectHash.Should().Be("hash-3");
    }

    private static string ValidArguments(string? extraFields = null)
    {
        var commaExtra = string.IsNullOrWhiteSpace(extraFields) ? string.Empty : "," + extraFields;
        return $$"""
            {
              "name": "plain-skill",
              "description": "Plain skill",
              "version": "1.0",
              "category": "plain",
              "instructions_markdown": "Do the work."
              {{commaExtra}}
            }
            """;
    }

    private static string ArgumentsWith(string replacementFields) =>
        $$"""
            {
              "name": "plain-skill",
              "description": "Plain skill",
              "version": "1.0",
              "category": "plain",
              "instructions_markdown": "Do the work.",
              {{replacementFields}}
            }
            """;

    private static OrnnPublishSkillTool CreateTool(
        CapturingHandler handler,
        IReadOnlyList<IOrnnSkillPublishAssetValidator>? validators = null)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        var options = new OrnnOptions { NyxIdSlug = "ornn" };
        var pipeline = new OrnnSkillPublishValidationPipeline(validators);
        var formatValidator = new OrnnSkillPackageFormatValidator(options, nyxClient);
        var client = new OrnnSkillClient(options, nyxClient);
        return new OrnnPublishSkillTool(
            pipeline,
            new OrnnSkillPackageBuilder(),
            formatValidator,
            client);
    }

    private static AgentToolContextScope BeginTokenScope() =>
        AgentToolContextScope.Push(global::TestAgentToolContexts.FromMetadata(new Dictionary<string, string>
        {
            [LLMRequestMetadataKeys.NyxIdAccessToken] = "caller-token",
        }));

    private sealed class StubValidator(
        string assetKind,
        params OrnnSkillPublishDiagnostic[] diagnostics) : IOrnnSkillPublishAssetValidator
    {
        public string AssetKind { get; } = assetKind;

        public Task<IReadOnlyList<OrnnSkillPublishDiagnostic>> ValidateAsync(
            OrnnSkillPublishRequest request,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OrnnSkillPublishDiagnostic>>(diagnostics);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Queue<CapturingResponse> _responses;

        public CapturingHandler(params string[] responses)
            : this(responses.Select(body => new CapturingResponse(body)).ToArray())
        {
        }

        public CapturingHandler(params CapturingResponse[] responses)
        {
            _responses = new Queue<CapturingResponse>(responses);
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = _responses.Count == 0
                ? new CapturingResponse("""{ "error": true }""")
                : _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body),
            });
        }
    }

    private sealed record CapturingResponse(string Body, HttpStatusCode StatusCode = HttpStatusCode.OK);
}
