using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.GAgents.WorkflowDelivery;
using Aevatar.Studio.Application.Delivery;
using FluentAssertions;
using YamlDotNet.RepresentationModel;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowDeliveryConfigurationRendererTests
{
    private const string SourceYaml = """
        name: workflow-alpha
        description: 'user_service_id: decoy'
        steps:
          - id: config
            parameters:
              value: '{"nested":{"threshold":10,"keep":"yes"}}'
          - id: call
            capability:
              nyxid_request:
                user_service_id: original-service
        """;

    [Fact]
    public void Render_ShouldResolveYamlAndEmbeddedJsonPointersAndStructuredConnectionIdentity()
    {
        var package = Package();
        var renderer = new WorkflowDeliveryConfigurationRenderer();

        var result = renderer.Render(
            package,
            new Dictionary<string, JsonElement>
            {
                ["threshold"] = Json("25"),
            },
            new Dictionary<string, string>
            {
                ["mail"] = " user-service-alpha ",
            });

        result.SourceHash.Should().Be(package.SourceHash);
        result.ResolvedHash.Should().NotBe(package.SourceHash);
        result.ConfigurationValues.Should().Contain("threshold", "25");
        result.ConnectionReferences.Should().Contain("mail", "user-service-alpha");
        package.SourceYaml.Should().Be(SourceYaml);

        var root = ParseRoot(result.ResolvedYaml);
        Scalar(root, "description").Value.Should().Be("user_service_id: decoy");
        var steps = (YamlSequenceNode)Child(root, "steps");
        var config = (YamlMappingNode)steps.Children[0];
        var parameters = (YamlMappingNode)Child(config, "parameters");
        var embedded = JsonNode.Parse(Scalar(parameters, "value").Value!);
        embedded!["nested"]!["threshold"]!.GetValue<int>().Should().Be(25);
        embedded["nested"]!["keep"]!.GetValue<string>().Should().Be("yes");

        var call = (YamlMappingNode)steps.Children[1];
        var capability = (YamlMappingNode)Child(call, "capability");
        var request = (YamlMappingNode)Child(capability, "nyxid_request");
        Scalar(request, "user_service_id").Value.Should().Be("user-service-alpha");
    }

    [Fact]
    public void Render_WhenConfigurationContainsUnknownField_ShouldRejectBeforeMutation()
    {
        var package = Package();
        var renderer = new WorkflowDeliveryConfigurationRenderer();

        var action = () => renderer.Render(
            package,
            new Dictionary<string, JsonElement>
            {
                ["unknown"] = Json("true"),
            },
            null);

        var exception = action.Should().Throw<WorkflowDeliveryConfigurationException>().Which;
        exception.Code.Should().Be("UNKNOWN_CONFIGURATION_FIELD");
        package.SourceYaml.Should().Be(SourceYaml);
    }

    [Fact]
    public void Render_WhenRequiredConfigurationFieldIsMissing_ShouldRejectInsteadOfKeepingPackageDefault()
    {
        var package = Package();
        var renderer = new WorkflowDeliveryConfigurationRenderer();

        var action = () => renderer.Render(
            package,
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, string>
            {
                ["mail"] = "user-service-alpha",
            });

        var exception = action.Should().Throw<WorkflowDeliveryConfigurationException>().Which;
        exception.Code.Should().Be("CONFIGURATION_FIELD_REQUIRED");
        exception.Message.Should().Contain("threshold");
        package.SourceYaml.Should().Be(SourceYaml);
    }

    [Fact]
    public void Render_WhenOptionalConfigurationFieldIsMissing_ShouldKeepPackageDefault()
    {
        var package = Package();
        package.VariableSchema.Add(new WorkflowDeliveryVariableDefinition
        {
            Key = "keep",
            Label = "Keep",
            Description = "Optional passthrough",
            Kind = WorkflowDeliveryVariableKind.String,
            Required = false,
            YamlPointer = "/steps/0/parameters/value",
            JsonPointer = "/nested/keep",
        });
        var renderer = new WorkflowDeliveryConfigurationRenderer();

        var result = renderer.Render(
            package,
            new Dictionary<string, JsonElement>
            {
                ["threshold"] = Json("25"),
            },
            new Dictionary<string, string>
            {
                ["mail"] = "user-service-alpha",
            });

        result.ResolvedYaml.Should().Contain("\"keep\":\"yes\"");
    }

    [Fact]
    public void Render_WhenOptionalConnectionSlotIsMissing_ShouldClearPackageDefault()
    {
        var package = Package();
        package.ConnectionSlots[0].Required = false;
        var renderer = new WorkflowDeliveryConfigurationRenderer();

        var result = renderer.Render(
            package,
            new Dictionary<string, JsonElement>
            {
                ["threshold"] = Json("25"),
            },
            null);

        result.ConnectionReferences.Should().BeEmpty();
        var root = ParseRoot(result.ResolvedYaml);
        var steps = (YamlSequenceNode)Child(root, "steps");
        var call = (YamlMappingNode)steps.Children[1];
        var capability = (YamlMappingNode)Child(call, "capability");
        var request = (YamlMappingNode)Child(capability, "nyxid_request");
        Scalar(request, "user_service_id").Value.Should().BeEmpty();
    }

    [Fact]
    public void Render_ShouldResolveMultipleConnectionSlotsByDeclaredYamlPointers()
    {
        const string sourceYaml = """
            name: merchant-intake-workflow
            description: 'user_service_id: decoy'
            steps:
              - id: calendar
                type: tool_call
                capability:
                  nyxid_request:
                    user_service_id: calendar-placeholder
              - id: document
                type: tool_call
                capability:
                  nyxid_request:
                    user_service_id: document-placeholder
            """;
        var package = new WorkflowPackageVersionSnapshot
        {
            PackageId = "merchant-intake-workflow",
            PackageVersionId = "merchant-intake-workflow@source-alpha",
            WorkflowName = "merchant-intake-workflow",
            Version = "1",
            DisplayName = "Merchant Intake Workflow",
            SourceYaml = sourceYaml,
            SourceHash = Hash(sourceYaml),
            CreatedBy = "admin-alpha",
        };
        package.ConnectionSlots.Add(new WorkflowDeliveryConnectionSlotDefinition
        {
            Key = "calendar",
            Label = "Calendar",
            ServiceSlug = "service-calendar",
            Required = true,
            YamlPointer = "/steps/0/capability/nyxid_request/user_service_id",
        });
        package.ConnectionSlots.Add(new WorkflowDeliveryConnectionSlotDefinition
        {
            Key = "document",
            Label = "Document",
            ServiceSlug = "service-document",
            Required = true,
            YamlPointer = "/steps/1/capability/nyxid_request/user_service_id",
        });
        var renderer = new WorkflowDeliveryConfigurationRenderer();

        var result = renderer.Render(
            package,
            null,
            new Dictionary<string, string>
            {
                ["calendar"] = "user-service-calendar",
                ["document"] = "user-service-document",
            });

        result.ConnectionReferences.Should().Contain("calendar", "user-service-calendar");
        result.ConnectionReferences.Should().Contain("document", "user-service-document");
        var root = ParseRoot(result.ResolvedYaml);
        Scalar(root, "description").Value.Should().Be("user_service_id: decoy");
        var steps = (YamlSequenceNode)Child(root, "steps");
        var calendar = (YamlMappingNode)Child((YamlMappingNode)Child((YamlMappingNode)steps.Children[0], "capability"), "nyxid_request");
        var document = (YamlMappingNode)Child((YamlMappingNode)Child((YamlMappingNode)steps.Children[1], "capability"), "nyxid_request");
        Scalar(calendar, "user_service_id").Value.Should().Be("user-service-calendar");
        Scalar(document, "user_service_id").Value.Should().Be("user-service-document");
    }

    private static WorkflowPackageVersionSnapshot Package()
    {
        var package = new WorkflowPackageVersionSnapshot
        {
            PackageId = "package-alpha",
            PackageVersionId = "package-alpha@source-alpha",
            WorkflowName = "workflow-alpha",
            Version = "1",
            DisplayName = "Workflow Alpha",
            SourceYaml = SourceYaml,
            SourceHash = Hash(SourceYaml),
            CreatedBy = "admin-alpha",
        };
        package.VariableSchema.Add(new WorkflowDeliveryVariableDefinition
        {
            Key = "threshold",
            Label = "Threshold",
            Description = "Approval threshold",
            Kind = WorkflowDeliveryVariableKind.Integer,
            Required = true,
            YamlPointer = "/steps/0/parameters/value",
            JsonPointer = "/nested/threshold",
        });
        package.ConnectionSlots.Add(new WorkflowDeliveryConnectionSlotDefinition
        {
            Key = "mail",
            Label = "Mail",
            ServiceSlug = "api-lark-bot",
            Required = true,
            YamlPointer = "/steps/1/capability/nyxid_request/user_service_id",
        });
        return package;
    }

    private static JsonElement Json(string value) =>
        JsonSerializer.Deserialize<JsonElement>(value);

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static YamlMappingNode ParseRoot(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents.Single().RootNode;
    }

    private static YamlNode Child(YamlMappingNode mapping, string key) =>
        mapping.Children.Single(pair =>
            pair.Key is YamlScalarNode scalar &&
            string.Equals(scalar.Value, key, StringComparison.Ordinal)).Value;

    private static YamlScalarNode Scalar(YamlMappingNode mapping, string key) =>
        (YamlScalarNode)Child(mapping, key);
}
