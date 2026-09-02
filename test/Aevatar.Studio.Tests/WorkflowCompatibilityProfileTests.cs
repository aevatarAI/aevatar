using System.Text;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Infrastructure.Serialization;
using Aevatar.Workflow.Abstractions.Workflows;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowCompatibilityProfileTests
{
    private readonly WorkflowCompatibilityProfile _profile = WorkflowCompatibilityProfile.AevatarV1;

    [Fact]
    public void AevatarV1_ShouldHaveExpectedVersion()
    {
        _profile.Version.Should().Be(WorkflowYamlRootSchema.Version);
    }

    [Fact]
    public void AevatarV1_ShouldConsumeSharedRootSchema()
    {
        _profile.RootFieldOrder.Should().Equal(WorkflowYamlRootSchema.AcceptedRootFieldOrder);
        _profile.AuthorableRootFieldOrder.Should().Equal(WorkflowYamlRootSchema.AuthorableRootFieldOrder);
        _profile.AllowedRootFields.Should().BeEquivalentTo(WorkflowYamlRootSchema.AuthorableRootFields);
        _profile.FormatRootFields().Should().Be(WorkflowYamlRootSchema.FormatAuthorableRootFields());
        _profile.FormatRejectedDialectRootFields().Should().Be(WorkflowYamlRootSchema.FormatUnsupportedDialectRootFields());
    }

    [Fact]
    public void WorkflowYamlRootSchema_ShouldKeepAuthorableRootFieldsParserAccepted()
    {
        WorkflowYamlRootSchema.AuthorableRootFieldOrder.Should()
            .OnlyContain(field => WorkflowYamlRootSchema.IsAcceptedRootField(field));
        WorkflowYamlRootSchema.AuthorableRootFields.Should()
            .BeSubsetOf(WorkflowYamlRootSchema.AcceptedRootFields);
    }

    [Theory]
    [MemberData(nameof(AuthorableRootFields))]
    public void AevatarV1_ShouldAcceptAuthorableRootFields(string rootField)
    {
        var yaml = BuildYamlWithRootField(rootField);

        var studioParse = new YamlWorkflowDocumentService(_profile).Parse(yaml);
        Action parserParse = () => new WorkflowParser().Parse(yaml);

        studioParse.Findings.Should().NotContain(finding =>
            string.Equals(finding.Code, "unknown_field", StringComparison.OrdinalIgnoreCase) &&
            finding.Path == $"/{rootField}");
        parserParse.Should().NotThrow();
    }

    [Theory]
    [MemberData(nameof(ParserOnlyRootFields))]
    public void AevatarV1_ShouldRejectParserOnlyRootFieldsInStudio(string rootField)
    {
        var yaml = BuildYamlWithRootField(rootField);

        var studioParse = new YamlWorkflowDocumentService(_profile).Parse(yaml);
        Action parserParse = () => new WorkflowParser().Parse(yaml);

        studioParse.Findings.Should().Contain(finding =>
            string.Equals(finding.Code, "unknown_field", StringComparison.OrdinalIgnoreCase) &&
            finding.Path == $"/{rootField}");
        parserParse.Should().NotThrow();
    }

    [Fact]
    public void Parse_WhenChildrenDepthIsBelowResourceLimit_ShouldCreateDocument()
    {
        var service = new YamlWorkflowDocumentService(_profile);

        var result = service.Parse(BuildNestedWorkflowYaml(childLinks: 30));

        result.Document.Should().NotBeNull();
        result.Findings.Should().NotContain(finding => finding.Code == "yaml_resource_limit");
    }

    [Fact]
    public void Parse_WhenChildrenDepthExceedsResourceLimit_ShouldRejectBeforeLoadingDocument()
    {
        var service = new YamlWorkflowDocumentService(_profile);

        var result = service.Parse(BuildNestedWorkflowYaml(childLinks: 31));

        result.Document.Should().BeNull();
        result.Findings.Should().ContainSingle(finding =>
            finding.Path == "/" && finding.Code == "yaml_resource_limit");
    }

    [Fact]
    public void Parse_WhenCollectionAliasCreatesCycle_ShouldRejectBeforeLoadingDocument()
    {
        var service = new YamlWorkflowDocumentService(_profile);
        const string yaml = """
                            name: cyclic
                            roles: []
                            steps: &steps
                              - id: loop
                                type: assign
                                children: *steps
                            """;

        var result = service.Parse(yaml);

        result.Document.Should().BeNull();
        result.Findings.Should().ContainSingle(finding =>
            finding.Path == "/" && finding.Code == "yaml_resource_limit");
    }

    [Fact]
    public void Parse_WhenForwardCollectionAliasesCreateCycle_ShouldRejectBeforeLoadingDocument()
    {
        var service = new YamlWorkflowDocumentService(_profile);
        const string yaml = """
                            name: forward-cycle
                            steps: *a
                            roles: &a
                              - id: a
                                type: assign
                                children: *b
                            configuration: &b
                              - id: b
                                type: assign
                                children: *a
                            """;

        var result = service.Parse(yaml);

        result.Document.Should().BeNull();
        result.Findings.Should().ContainSingle(finding =>
            finding.Path == "/" && finding.Code == "yaml_resource_limit");
    }

    [Fact]
    public void Parse_WhenRuntimeAllowedToolsFieldsDeclared_ShouldAcceptTypedFieldsAndRoundTripToRuntime()
    {
        var service = new YamlWorkflowDocumentService(_profile);
        var yaml = """
            name: tool_scope
            roles:
              - id: planner
                allowed_tools: [search, calendar]
              - id: isolated
                allowed_tools: []
              - id: inherited
            steps:
              - id: scoped
                type: llm_call
                target_role: planner
                allowed_tools: [calendar]
                parameters:
                  prompt_prefix: "Use scoped tool"
              - id: no_tools
                type: llm_call
                target_role: isolated
                allowed_tools: []
              - id: inherited_tools
                type: llm_call
                target_role: inherited
            """;

        var studioParse = service.Parse(yaml);

        studioParse.Findings.Should().NotContain(finding =>
            string.Equals(finding.Code, "unknown_field", StringComparison.OrdinalIgnoreCase));
        studioParse.Document.Should().NotBeNull();
        var document = studioParse.Document!;
        document.Roles[0].AllowedTools.Should().Equal("search", "calendar");
        document.Roles[1].AllowedTools.Should().BeEmpty();
        document.Roles[2].AllowedTools.Should().BeNull();
        document.Steps[0].AllowedTools.Should().Equal("calendar");
        document.Steps[1].AllowedTools.Should().BeEmpty();
        document.Steps[2].AllowedTools.Should().BeNull();

        var serialized = service.Serialize(document);
        var studioRoundTrip = service.Parse(serialized);
        studioRoundTrip.Findings.Should().NotContain(finding =>
            string.Equals(finding.Code, "unknown_field", StringComparison.OrdinalIgnoreCase));
        studioRoundTrip.Document!.Roles[0].AllowedTools.Should().Equal("search", "calendar");
        studioRoundTrip.Document.Roles[1].AllowedTools.Should().BeEmpty();
        studioRoundTrip.Document.Roles[2].AllowedTools.Should().BeNull();
        studioRoundTrip.Document.Steps[0].AllowedTools.Should().Equal("calendar");
        studioRoundTrip.Document.Steps[1].AllowedTools.Should().BeEmpty();
        studioRoundTrip.Document.Steps[2].AllowedTools.Should().BeNull();

        var runtimeRoundTrip = new WorkflowParser().Parse(serialized);
        runtimeRoundTrip.Roles[0].AgentToolScope!.AllowedToolNames.Should().Equal("search", "calendar");
        runtimeRoundTrip.Roles[1].AgentToolScope!.AllowedToolNames.Should().BeEmpty();
        runtimeRoundTrip.Roles[2].AgentToolScope.Should().BeNull();
        runtimeRoundTrip.Steps[0].AgentToolScope!.AllowedToolNames.Should().Equal("calendar");
        runtimeRoundTrip.Steps[1].AgentToolScope!.AllowedToolNames.Should().BeEmpty();
        runtimeRoundTrip.Steps[2].AgentToolScope.Should().BeNull();
    }

    [Fact]
    public void Parse_WhenAllowedToolsScalarContainsNonRuntimeDelimiters_ShouldPreserveRuntimeTokenization()
    {
        var service = new YamlWorkflowDocumentService(_profile);
        var yaml = """
            name: tool_scope_scalar
            roles:
              - id: planner
                allowed_tools: "search;calendar"
            steps:
              - id: scoped
                type: llm_call
                target_role: planner
                allowed_tools: |-
                  calendar
                  email
            """;

        var studioParse = service.Parse(yaml);

        studioParse.Document.Should().NotBeNull();
        var document = studioParse.Document!;
        document.Roles[0].AllowedTools.Should().Equal("search;calendar");
        document.Steps[0].AllowedTools.Should().Equal("calendar\nemail");

        var runtimeParse = new WorkflowParser().Parse(yaml);
        runtimeParse.Roles[0].AgentToolScope!.AllowedToolNames.Should().Equal("search;calendar");
        runtimeParse.Steps[0].AgentToolScope!.AllowedToolNames.Should().Equal("calendar\nemail");

        var runtimeRoundTrip = new WorkflowParser().Parse(service.Serialize(document));
        runtimeRoundTrip.Roles[0].AgentToolScope!.AllowedToolNames.Should().Equal("search;calendar");
        runtimeRoundTrip.Steps[0].AgentToolScope!.AllowedToolNames.Should().Equal("calendar\nemail");
    }

    [Theory]
    [InlineData("version")]
    [InlineData("inputs")]
    [InlineData("outputs")]
    [InlineData("triggers")]
    [InlineData("on")]
    [InlineData("env")]
    [InlineData("jobs")]
    public void AevatarV1_ShouldRejectOtherDialectRootFields(string field)
    {
        _profile.AllowedRootFields.Should().NotContain(field);
    }

    public static IEnumerable<object[]> AuthorableRootFields() =>
        WorkflowYamlRootSchema.AuthorableRootFieldOrder.Select(static field => new object[] { field });

    public static IEnumerable<object[]> ParserOnlyRootFields() =>
        WorkflowYamlRootSchema.AcceptedRootFieldOrder
            .Where(static field => !WorkflowYamlRootSchema.AuthorableRootFields.Contains(field))
            .Select(static field => new object[] { field });

    private static string BuildYamlWithRootField(string rootField) =>
        rootField switch
        {
            "name" => """
                name: monitor
                steps: []
                """,
            "description" => """
                name: monitor
                description: Test workflow
                steps: []
                """,
            "when_to_use" => """
                name: monitor
                when_to_use: Use when monitoring is needed.
                steps: []
                """,
            "configuration" => """
                name: monitor
                configuration:
                  closed_world_mode: true
                steps: []
                """,
            "roles" => """
                name: monitor
                roles:
                  - id: analyst
                    name: Analyst
                steps: []
                """,
            "steps" => """
                name: monitor
                steps:
                  - id: step_1
                    type: llm_call
                """,
            "on_failure" => """
                name: monitor
                on_failure:
                  action: fail
                  max_attempts: 1
                steps: []
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(rootField), rootField, null),
        };

    private static string BuildNestedWorkflowYaml(int childLinks)
    {
        var yaml = new StringBuilder()
            .AppendLine("name: nested")
            .AppendLine("roles: []")
            .AppendLine("steps:");

        for (var index = 0; index <= childLinks; index++)
        {
            var itemIndent = new string(' ', 2 + (index * 4));
            var propertyIndent = new string(' ', 4 + (index * 4));
            yaml.Append(itemIndent).Append("- id: step_").AppendLine(index.ToString());
            yaml.Append(propertyIndent).AppendLine("type: assign");
            if (index < childLinks)
                yaml.Append(propertyIndent).AppendLine("children:");
        }

        return yaml.ToString();
    }

    [Theory]
    [InlineData("llm", "llm_call")]
    [InlineData("chat", "llm_call")]
    [InlineData("task", "llm_call")]
    [InlineData("loop", "while")]
    [InlineData("sub_workflow", "workflow_call")]
    [InlineData("foreach_llm", "foreach")]
    [InlineData("http_get", "connector_call")]
    [InlineData("http_post", "connector_call")]
    [InlineData("mcp_call", "connector_call")]
    [InlineData("sleep", "delay")]
    [InlineData("publish", "emit")]
    [InlineData("vote_consensus", "vote")]
    public void ToCanonicalType_ShouldResolveAliases(string alias, string expected)
    {
        _profile.ToCanonicalType(alias).Should().Be(expected);
    }

    [Theory]
    [InlineData("transform")]
    [InlineData("conditional")]
    [InlineData("llm_call")]
    [InlineData("workflow_call")]
    public void ToCanonicalType_ShouldReturnCanonicalAsIs(string type)
    {
        _profile.ToCanonicalType(type).Should().Be(type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ToCanonicalType_ShouldReturnEmptyForNullOrBlank(string? value)
    {
        _profile.ToCanonicalType(value).Should().BeEmpty();
    }

    [Theory]
    [InlineData("  LOOP  ", "while")]
    [InlineData("Transform", "transform")]
    public void ToCanonicalType_ShouldBeCaseInsensitiveAndTrim(string value, string expected)
    {
        _profile.ToCanonicalType(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("transform", true)]
    [InlineData("loop", true)]
    [InlineData("actor_send", true)]
    [InlineData("workflow_loop", true)]
    [InlineData("nonexistent", false)]
    [InlineData(null, false)]
    public void IsKnownStepType_ShouldRecognizeAllRegisteredTypes(string? type, bool expected)
    {
        _profile.IsKnownStepType(type).Should().Be(expected);
    }

    [Theory]
    [InlineData("transform", true)]
    [InlineData("actor_send", false)]
    [InlineData("workflow_loop", false)]
    public void IsCanonicalStepType_ShouldOnlyMatchCanonical(string type, bool expected)
    {
        _profile.IsCanonicalStepType(type).Should().Be(expected);
    }

    [Fact]
    public void IsAdvancedImportOnly_ShouldMatchActorSend()
    {
        _profile.IsAdvancedImportOnly("actor_send").Should().BeTrue();
        _profile.IsAdvancedImportOnly("transform").Should().BeFalse();
    }

    [Fact]
    public void IsForbiddenAuthoringType_ShouldMatchWorkflowLoop()
    {
        _profile.IsForbiddenAuthoringType("workflow_loop").Should().BeTrue();
        _profile.IsForbiddenAuthoringType("while").Should().BeFalse();
    }

    [Theory]
    [InlineData("sub_step_type", true)]
    [InlineData("map_step_type", true)]
    [InlineData("step", true)]
    [InlineData("prompt", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsStepTypeParameterKey_ShouldMatchExpectedKeys(string? key, bool expected)
    {
        _profile.IsStepTypeParameterKey(key).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("singleton", true)]
    [InlineData("transient", true)]
    [InlineData("scope", true)]
    [InlineData("unknown", false)]
    public void IsSupportedWorkflowCallLifecycle_ShouldValidateLifecycles(string? value, bool expected)
    {
        _profile.IsSupportedWorkflowCallLifecycle(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("wait_signal", true)]
    [InlineData("connector_call", true)]
    [InlineData("llm_call", true)]
    [InlineData("human_input", true)]
    [InlineData("human_approval", true)]
    [InlineData("transform", false)]
    [InlineData("conditional", false)]
    public void ShouldMirrorTimeoutMsToParameters_ShouldMatchExpectedTypes(string type, bool expected)
    {
        _profile.ShouldMirrorTimeoutMsToParameters(type).Should().Be(expected);
    }

    [Fact]
    public void AllowedRoleFields_ShouldRejectRetiredStreamBufferCapacity()
    {
        _profile.AllowedRoleFields.Should().NotContain("stream_buffer_capacity");
    }

    [Fact]
    public void Parse_WhenRoleUsesRetiredStreamBufferCapacity_ShouldReportUnknownField()
    {
        var service = new YamlWorkflowDocumentService(_profile);

        var result = service.Parse("""
            name: retired-field
            roles:
              - id: assistant
                stream_buffer_capacity: 128
            steps:
              - id: ask
                type: llm_call
                target_role: assistant
            """);

        result.Findings.Should().Contain(f =>
            f.Path == "/roles/0/stream_buffer_capacity" &&
            f.Code == "unknown_field");
    }

    [Fact]
    public void Serialize_WhenRoleHasStreamSettings_ShouldOmitRetiredStreamBufferCapacity()
    {
        var service = new YamlWorkflowDocumentService(_profile);
        var document = new WorkflowDocument
        {
            Name = "retired-field",
            Roles =
            [
                new RoleModel
                {
                    Id = "assistant",
                    MaxHistoryMessages = 32,
                    EventModules = "llm_handler",
                },
            ],
        };

        var yaml = service.Serialize(document);

        yaml.Should().Contain("max_history_messages");
        yaml.Should().Contain("event_modules");
        yaml.Should().NotContain("stream_buffer_capacity");
    }
}
