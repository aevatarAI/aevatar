using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using FluentAssertions;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnUpdateSkillToolTests
{
    private const string SkillId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void ToolMetadata_ShouldExposeUpdateToolWithNarrowMutationContract()
    {
        var tool = CreateTool(new CapturingHandler("""{ "data": { "valid": true } }"""));

        tool.Name.Should().Be("ornn_update_skill");
        tool.ApprovalMode.Should().Be(ToolApprovalMode.Auto);
        tool.IsReadOnly.Should().BeFalse();
        tool.IsDestructive.Should().BeFalse();
        tool.SideEffectKind.Should().Be("ornn.update.skill");
        tool.Description.Should().Contain("search or GET the current skill JSON");
        tool.Description.Should().Contain("stable skill_id");
        tool.Description.Should().Contain("templates/import sources");
        tool.Description.Should().Contain("not Scope Workflow runtime publication");

        using var schema = JsonDocument.Parse(tool.ParametersSchema);
        var root = schema.RootElement;
        root.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        var properties = root.GetProperty("properties").EnumerateObject().Select(x => x.Name).ToArray();
        properties.Should().Equal(
            "skill_id",
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
        root.GetProperty("required").EnumerateArray().Select(x => x.GetString()).Should().Equal(
            "skill_id",
            "name",
            "description",
            "version",
            "category",
            "instructions_markdown");
        properties.Should().NotContain([
            "skill_name",
            "id_or_name",
            "credential",
            "token",
            "owner",
            "service_url",
            "metadata",
            "raw_file_map",
            "skip_validation"
        ]);
    }

    [Fact]
    public void ToolDescription_ShouldNotHardcodeProductionSkillNames()
    {
        var tool = CreateTool(new CapturingHandler("""{ "data": { "valid": true } }"""));

        var text = tool.Description + tool.ParametersSchema;

        text.Should().NotContain("/daily");
        text.Should().NotContain("chrono-ai-daily");
        text.Should().NotContain("project-summary");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRejectMissingCallerToken()
    {
        var tool = CreateTool(new CapturingHandler("""{ "data": { "valid": true } }"""));
        AgentToolRequestContext.Current = null;

        var result = await tool.ExecuteAsync(ValidArguments());

        result.Should().Contain("No NyxID access token");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task ExecuteAsync_ShouldRejectMissingOrInvalidSkillIdBeforeUpload(string skillId)
    {
        var handler = new CapturingHandler("""{ "data": { "valid": true } }""");
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ValidArguments(skillId: skillId));

        result.Should().Contain("validation_error");
        result.Should().Contain(string.IsNullOrEmpty(skillId) ? "missing_field" : "invalid_skill_id");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("skill_name")]
    [InlineData("id_or_name")]
    [InlineData("credential")]
    [InlineData("token")]
    [InlineData("owner")]
    [InlineData("service_url")]
    [InlineData("metadata")]
    [InlineData("raw_file_map")]
    [InlineData("skip_validation")]
    public async Task ExecuteAsync_ShouldRejectForbiddenRootFieldsBeforeUpload(string field)
    {
        var handler = new CapturingHandler("""{ "data": { "valid": true } }""");
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ValidArguments(extraFields: $$"""
            "{{field}}": "bad"
            """));

        result.Should().Contain("validation_error");
        result.Should().Contain("unknown_field");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSkipUploadWhenLocalValidationFailsAfterStrippingSkillId()
    {
        var handler = new CapturingHandler("""{ "data": { "valid": true } }""");
        var tool = CreateTool(handler, validators:
        [
            new StubValidator(
                OrnnSkillPublishValidationPipeline.ScriptAssetKind,
                new OrnnSkillPublishDiagnostic("bad_script", "bad script"))
        ]);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ValidArguments(extraFields: """
            "scripts": [{ "path": "main.cs", "content": "class C {}" }]
            """));

        result.Should().Contain("bad_script");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSkipUpdateWhenOrnnFormatValidationFails()
    {
        var handler = new CapturingHandler("""
            { "data": { "valid": false, "violations": [{ "rule": "skill-md", "message": "bad" }] } }
            """);
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ValidArguments());

        result.Should().Contain("format_validation_error");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be(
            "/api/v1/proxy/s/ornn/api/v1/skill-format/validate");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldValidateThenUpdateBySkillIdOnSuccess()
    {
        var handler = new CapturingHandler(
            """{ "data": { "valid": true, "violations": [] } }""",
            """{ "data": { "id": "11111111-2222-3333-4444-555555555555", "version": "1.2", "skillHash": "hash-1" } }""");
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ValidArguments());

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        root.GetProperty("status").GetString().Should().Be("success");
        root.GetProperty("skill_id").GetString().Should().Be(SkillId);
        root.GetProperty("guid").GetString().Should().Be(SkillId);
        root.GetProperty("version").GetString().Should().Be("1.2");
        root.GetProperty("skillHash").GetString().Should().Be("hash-1");

        handler.Requests.Select(x => x.RequestUri!.AbsolutePath).Should().Equal(
            "/api/v1/proxy/s/ornn/api/v1/skill-format/validate",
            $"/api/v1/proxy/s/ornn/api/v1/skills/{SkillId}");
        handler.Requests.Select(x => x.Method).Should().Equal(HttpMethod.Post, HttpMethod.Put);
        handler.Requests.Select(x => x.Content!.Headers.ContentType!.MediaType).Should().Equal(
            "application/zip",
            "application/zip");
    }

    [Fact]
    public async Task ExecuteAsync_WhenUpdateReturnsPermissionError_ShouldSurfaceUpstreamReason()
    {
        var handler = new CapturingHandler(
            new CapturingResponse("""{ "data": { "valid": true, "violations": [] } }"""),
            new CapturingResponse(
                """{ "status": 403, "code": "permission_denied", "detail": "Missing ornn:skill:update permission" }""",
                HttpStatusCode.Forbidden));
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ValidArguments());

        result.Should().Contain("\"status\":\"error\"");
        result.Should().Contain("Missing ornn:skill:update permission");
        result.Should().NotContain("missing proxy scope");
    }

    [Fact]
    public async Task CreateSuccessReceipt_ShouldMapUpdatedSkillSubjectFromToolResult()
    {
        var handler = new CapturingHandler(
            """{ "data": { "valid": true, "violations": [] } }""",
            """{ "data": { "id": "11111111-2222-3333-4444-555555555555", "hash": "hash-3" } }""");
        var tool = CreateTool(handler);

        using var _ = BeginTokenScope();
        var result = await tool.ExecuteAsync(ValidArguments());
        var receipt = tool.CreateSuccessReceipt("call-1", tool.Name, result);

        receipt.Should().NotBeNull();
        receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
        receipt.ApprovalMode.Should().Be(AgentToolReceiptApprovalMode.Auto);
        receipt.IsDestructive.Should().BeFalse();
        receipt.SideEffectKind.Should().Be("ornn.update.skill");
        receipt.SubjectKind.Should().Be("ornn.skill");
        receipt.SubjectId.Should().Be(SkillId);
        receipt.SubjectVersion.Should().Be("1.0");
        receipt.SubjectHash.Should().Be("hash-3");
    }

    private static string ValidArguments(string skillId = SkillId, string? extraFields = null)
    {
        var commaExtra = string.IsNullOrWhiteSpace(extraFields) ? string.Empty : "," + extraFields;
        return $$"""
            {
              "skill_id": "{{skillId}}",
              "name": "plain-skill",
              "description": "Plain skill",
              "version": "1.0",
              "category": "plain",
              "instructions_markdown": "Do the work."
              {{commaExtra}}
            }
            """;
    }

    private static OrnnUpdateSkillTool CreateTool(
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
        return new OrnnUpdateSkillTool(
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
