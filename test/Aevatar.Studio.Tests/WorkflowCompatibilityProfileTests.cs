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

    [Theory]
    [InlineData("llm", "llm_call")]
    [InlineData("chat", "llm_call")]
    [InlineData("task", "llm_call")]
    [InlineData("loop", "while")]
    [InlineData("sub_workflow", "workflow_call")]
    [InlineData("foreach_llm", "foreach")]
    [InlineData("http_get", "http_request")]
    [InlineData("http_post", "http_request")]
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
    [InlineData("http_request")]
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
    [InlineData("http_request", true)]
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
    [InlineData("http_request", true)]
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
