using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Net.Http.Json;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Domain.Studio.Services;
using Aevatar.Studio.Hosting.Controllers;
using Aevatar.Studio.Infrastructure.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Tests;

public sealed class EditorControllerSerializationTests
{
    [Fact]
    public void EditorWorkflowHttpRequests_ShouldUseScopedWorkflowDocumentInputConverter()
    {
        typeof(ParseYamlHttpResponse).GetProperty(nameof(ParseYamlHttpResponse.Document))!
            .ShouldUseWorkflowDocumentInputConverter();
        typeof(SerializeYamlHttpResponse).GetProperty(nameof(SerializeYamlHttpResponse.Document))!
            .ShouldUseWorkflowDocumentInputConverter();
        typeof(SerializeYamlHttpRequest).GetProperty(nameof(SerializeYamlHttpRequest.Document))!
            .ShouldUseWorkflowDocumentInputConverter();
        typeof(ValidateWorkflowHttpRequest).GetProperty(nameof(ValidateWorkflowHttpRequest.Document))!
            .ShouldUseWorkflowDocumentInputConverter();
        typeof(NormalizeWorkflowHttpRequest).GetProperty(nameof(NormalizeWorkflowHttpRequest.Document))!
            .ShouldUseWorkflowDocumentInputConverter();
        typeof(NormalizeWorkflowHttpResponse).GetProperty(nameof(NormalizeWorkflowHttpResponse.Document))!
            .ShouldUseWorkflowDocumentInputConverter();

        typeof(SerializeYamlHttpRequest).Assembly
            .GetType("Aevatar.Studio.Hosting.Controllers.EditorWorkflowDocumentDto")
            .Should()
            .BeNull("the Host boundary should not duplicate the WorkflowDocument DTO graph");
    }

    [Fact]
    public async Task SerializeYaml_ShouldAcceptPlainJsonStepParameters()
    {
        using var host = await StartHostAsync();
        var client = host.GetTestClient();

        using var response = await client.PostAsJsonAsync("/api/editor/serialize-yaml", BuildPlainParameterRequest());

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("target: result");
        body.Should().Contain("value: $input");
        body.Should().Contain("enabled: true");
        body.Should().Contain("limit: 3");
        body.Should().Contain("empty:");
        body.Should().Contain("items:");
        body.Should().Contain("- one");
        body.Should().Contain("nested:");
        body.Should().Contain("inner: value");
    }

    [Theory]
    [InlineData("/api/editor/serialize-yaml")]
    [InlineData("/api/editor/normalize")]
    public async Task DocumentEditorResponses_ShouldReturnPlainJsonStepParameters(string path)
    {
        using var host = await StartHostAsync();
        var client = host.GetTestClient();

        using var response = await client.PostAsJsonAsync(path, BuildPlainParameterRequest());

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\"target\":\"result\"");
        body.Should().Contain("\"value\":\"$input\"");
        body.Should().Contain("\"enabled\":\"true\"");
        body.Should().Contain("\"limit\":\"3\"");
        body.Should().Contain("\"items\":[\"one\",\"2\",null]");
        body.Should().Contain("\"nested\":{\"inner\":\"value\"}");
        body.Should().NotContain("\"target\":{}");
        body.Should().NotContain("\"value\":{}");
    }

    [Fact]
    public async Task SerializeYaml_ShouldPreserveResponseDocumentScalarsOnSecondRoundTrip()
    {
        using var host = await StartHostAsync();
        var client = host.GetTestClient();

        using var firstResponse = await client.PostAsJsonAsync("/api/editor/serialize-yaml", BuildPlainParameterRequest());
        var firstJson = await firstResponse.Content.ReadAsStringAsync();
        using var firstDocument = JsonDocument.Parse(firstJson);
        var document = firstDocument.RootElement.GetProperty("document").Clone();

        using var secondResponse = await client.PostAsJsonAsync("/api/editor/serialize-yaml", new
        {
            document,
            availableStepTypes = new[] { "assign" },
        });

        var body = await secondResponse.Content.ReadAsStringAsync();
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("target: result");
        body.Should().Contain("value: $input");
        body.Should().NotContain("target: {}");
        body.Should().NotContain("value: {}");
    }

    [Theory]
    [InlineData("/api/editor/validate")]
    [InlineData("/api/editor/normalize")]
    public async Task DocumentEditorEndpoints_ShouldAcceptPlainJsonStepParameters(string path)
    {
        using var host = await StartHostAsync();
        var client = host.GetTestClient();

        using var response = await client.PostAsJsonAsync(path, BuildPlainParameterRequest());

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain("could not be converted");
        body.Should().NotContain("StudioStepParameterValue");
    }

    [Fact]
    public void SerializeYamlHttpRequest_ShouldSerializeWithoutConverterWriteFailure()
    {
        var request = new SerializeYamlHttpRequest(new WorkflowDocument
        {
            Name = "draft",
            Steps =
            [
                new StepModel
                {
                    Id = "assign",
                    Type = "assign",
                    Parameters = new StudioStepParameters
                    {
                        ["target"] = StudioStepParameterValue.FromScalar("result"),
                    },
                },
            ],
        });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var json = JsonSerializer.Serialize(request, options);

        json.Should().Contain("\"document\"");
        json.Should().Contain("\"parameters\":{\"target\":\"result\"}");
    }

    [Fact]
    public async Task SerializeYaml_ShouldNotAcceptSnakeCaseJsonStepAliases()
    {
        using var host = await StartHostAsync();
        var client = host.GetTestClient();
        using var content = new StringContent(
            """
            {
              "document": {
                "name": "draft",
                "description": "",
                "configuration": { "closedWorldMode": false },
                "roles": [],
                "steps": [
                  {
                    "id": "assign",
                    "type": "assign",
                    "originalType": "assign",
                    "target_role": "writer",
                    "parameters": { "target": "result", "value": "$input" },
                    "branches": {}
                  }
                ]
              },
              "availableWorkflowNames": [ "draft" ],
              "availableStepTypes": [ "assign" ]
            }
            """,
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync("/api/editor/serialize-yaml", content);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain("target_role: writer");
    }

    [Fact]
    public async Task ParseYaml_ShouldReturnPlainJsonStepParameters()
    {
        using var host = await StartHostAsync();
        var client = host.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/editor/parse-yaml", new
        {
            yaml = """
                   name: draft
                   steps:
                     - id: clean
                       type: tool_call
                       parameters:
                         tool: code_execute
                         arguments:
                           language: javascript
                           code: |
                             console.log("ok");
                       next: capture
                     - id: capture
                       type: assign
                       parameters:
                         target: result
                         value: "$input"
                   """,
            availableStepTypes = new[] { "tool_call", "assign" },
        });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\"tool\":\"code_execute\"");
        body.Should().Contain("\"arguments\":{\"language\":\"javascript\",\"code\":\"console.log(\\\"ok\\\");\\n\"}");
        body.Should().Contain("\"target\":\"result\"");
        body.Should().Contain("\"value\":\"$input\"");
        body.Should().NotContain("\"tool\":{}");
        body.Should().NotContain("\"arguments\":{}");
    }

    [Fact]
    public async Task ParseAndSerializeYaml_ShouldPreserveAllowedToolsInDocumentJson()
    {
        using var host = await StartHostAsync();
        var client = host.GetTestClient();

        using var parseResponse = await client.PostAsJsonAsync("/api/editor/parse-yaml", new
        {
            yaml = """
                   name: tool_scope
                   roles:
                     - id: planner
                       allowed_tools: [search, calendar]
                       tool_sets: [nyxid.connected_services]
                     - id: isolated
                       allowed_tools: []
                   steps:
                     - id: scoped
                       type: llm_call
                       target_role: planner
                       allowed_tools: [calendar]
                       tool_sets: [nyxid.connected_services]
                     - id: no_tools
                       type: llm_call
                       target_role: isolated
                       allowed_tools: []
                       tool_sets: []
                   """,
            availableStepTypes = new[] { "llm_call" },
        });

        var parseBody = await parseResponse.Content.ReadAsStringAsync();
        parseResponse.StatusCode.Should().Be(HttpStatusCode.OK, parseBody);
        parseBody.Should().NotContain("\"code\":\"unknown_field\"");
        parseBody.Should().Contain("\"allowedTools\":[\"search\",\"calendar\"]");
        parseBody.Should().Contain("\"toolSets\":[\"nyxid.connected_services\"]");
        parseBody.Should().Contain("\"allowedTools\":[\"calendar\"]");
        parseBody.Should().Contain("\"toolSets\":[]");
        parseBody.Should().Contain("\"allowedTools\":[]");

        using var parsedJson = JsonDocument.Parse(parseBody);
        var document = parsedJson.RootElement.GetProperty("document").Clone();
        using var serializeResponse = await client.PostAsJsonAsync("/api/editor/serialize-yaml", new
        {
            document,
            availableStepTypes = new[] { "llm_call" },
        });

        var serializeBody = await serializeResponse.Content.ReadAsStringAsync();
        serializeResponse.StatusCode.Should().Be(HttpStatusCode.OK, serializeBody);
        serializeBody.Should().Contain("allowed_tools:");
        serializeBody.Should().Contain("tool_sets:");
        serializeBody.Should().Contain("- search");
        serializeBody.Should().Contain("- calendar");
        serializeBody.Should().Contain("allowed_tools: []");
        serializeBody.Should().Contain("tool_sets: []");
    }

    [Fact]
    public async Task ParseAndSerializeYaml_ShouldPreserveTypedNyxIdCapabilitiesRecursively()
    {
        using var host = await StartHostAsync();
        var client = host.GetTestClient();
        using var parseResponse = await client.PostAsJsonAsync("/api/editor/parse-yaml", new
        {
            yaml = """
                   name: typed_capabilities
                   steps:
                     - id: catalog_call
                       type: tool_call
                       capability:
                         nyxid_operation:
                           user_service_id: usvc-alpha
                           endpoint_id: endpoint-alpha
                       parameters:
                         tool: nyxid_proxy
                     - id: nested
                       type: parallel
                       children:
                         - id: explicit_call
                           type: tool_call
                           capability:
                             nyxid_request:
                               user_service_id: usvc-beta
                               method: GET
                               path_template: /api/resources/{resource_id}
                               query_parameters: [page_size]
                               body_mode: none
                               body_required: true
                               response_mode: file_artifact
                               risk: read_only
                           parameters:
                             tool: nyxid_proxy
                   """,
            availableStepTypes = new[] { "tool_call", "parallel" },
        });

        var parseBody = await parseResponse.Content.ReadAsStringAsync();
        parseResponse.StatusCode.Should().Be(HttpStatusCode.OK, parseBody);
        parseBody.Should().NotContain("\"code\":\"unknown_field\"");
        parseBody.Should().Contain("\"nyxIdOperation\":{\"userServiceId\":\"usvc-alpha\",\"endpointId\":\"endpoint-alpha\"}");
        parseBody.Should().Contain("\"nyxIdRequest\":{\"userServiceId\":\"usvc-beta\",\"method\":\"GET\",\"pathTemplate\":\"/api/resources/{resource_id}\"");
        parseBody.Should().Contain("\"bodyRequired\":true");
        parseBody.Should().Contain("\"risk\":\"read_only\"");

        var document = JsonNode.Parse(parseBody)!["document"]!.DeepClone();
        document["description"] = "unrelated edit";
        using var normalizeResponse = await client.PostAsJsonAsync("/api/editor/normalize", new
        {
            document,
            availableStepTypes = new[] { "tool_call", "parallel" },
        });

        var normalizeBody = await normalizeResponse.Content.ReadAsStringAsync();
        normalizeResponse.StatusCode.Should().Be(HttpStatusCode.OK, normalizeBody);
        normalizeBody.Should().Contain("\"description\":\"unrelated edit\"");
        normalizeBody.Should().Contain("\"bodyRequired\":true");
        normalizeBody.Should().Contain("body_required: true");
        normalizeBody.Should().Contain("risk: read_only");

        var normalizedDocument = JsonNode.Parse(normalizeBody)!["document"]!.DeepClone();
        using var serializeResponse = await client.PostAsJsonAsync("/api/editor/serialize-yaml", new
        {
            document = normalizedDocument,
            availableStepTypes = new[] { "tool_call", "parallel" },
        });

        var serializeBody = await serializeResponse.Content.ReadAsStringAsync();
        serializeResponse.StatusCode.Should().Be(HttpStatusCode.OK, serializeBody);
        serializeBody.Should().Contain("nyxid_operation:");
        serializeBody.Should().Contain("endpoint_id: endpoint-alpha");
        serializeBody.Should().Contain("nyxid_request:");
        serializeBody.Should().Contain("path_template: /api/resources/{resource_id}");
        serializeBody.Should().Contain("body_required: true");
        serializeBody.Should().Contain("response_mode: file_artifact");
        serializeBody.Should().Contain("risk: read_only");
    }

    [Fact]
    public async Task ParseYaml_ShouldRejectUnknownCapabilitySelector()
    {
        using var host = await StartHostAsync();
        var client = host.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/editor/parse-yaml", new
        {
            yaml = """
                   name: invalid_capability
                   steps:
                     - id: call
                       type: tool_call
                       capability:
                         guessed_proxy:
                           service: anything
                       parameters:
                         tool: nyxid_proxy
                   """,
            availableStepTypes = new[] { "tool_call" },
        });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\"path\":\"/steps/0/capability/guessed_proxy\"");
        body.Should().Contain("\"code\":\"unknown_field\"");
    }

    [Theory]
    [InlineData("nyxid_operation")]
    [InlineData("nyxid_request")]
    public async Task ParseYaml_ShouldRejectNonMappingCapabilitySelector(string selector)
    {
        using var host = await StartHostAsync();
        var client = host.GetTestClient();
        using var response = await client.PostAsJsonAsync("/api/editor/parse-yaml", new
        {
            yaml = $$"""
                   name: invalid_capability
                   steps:
                     - id: call
                       type: tool_call
                       capability:
                         {{selector}}: invalid-scalar
                       parameters:
                         tool: nyxid_proxy
                   """,
            availableStepTypes = new[] { "tool_call" },
        });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain($"\"path\":\"/steps/0/capability/{selector}\"");
        body.Should().Contain("\"code\":\"invalid_field\"");
    }

    private static object BuildPlainParameterRequest() => new
    {
        document = new
        {
            name = "draft",
            description = "",
            configuration = new { closedWorldMode = false },
            roles = Array.Empty<object>(),
            steps = new[]
            {
                new
                {
                    id = "assign",
                    type = "assign",
                    originalType = "assign",
                    targetRole = (string?)null,
                    parameters = new
                    {
                        target = "result",
                        value = "$input",
                        enabled = true,
                        limit = 3,
                        empty = (string?)null,
                        items = new object?[] { "one", 2, null },
                        nested = new { inner = "value" },
                    },
                    next = (string?)null,
                    branches = new Dictionary<string, string>(),
                },
            },
        },
        availableWorkflowNames = new[] { "draft" },
        availableStepTypes = new[] { "assign" },
    };

    private static async Task<IHost> StartHostAsync()
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    var profile = WorkflowCompatibilityProfile.AevatarV1;
                    services
                        .AddRouting()
                        .AddSingleton(profile)
                        .AddSingleton<IWorkflowYamlDocumentService, YamlWorkflowDocumentService>()
                        .AddSingleton<WorkflowDocumentNormalizer>()
                        .AddSingleton<WorkflowValidator>()
                        .AddSingleton<WorkflowGraphMapper>()
                        .AddSingleton<TextDiffService>()
                        .AddSingleton<WorkflowEditorService>();
                    services.AddControllers()
                        .AddApplicationPart(typeof(EditorController).Assembly)
                        .AddJsonOptions(json =>
                        {
                            json.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                            json.JsonSerializerOptions.DefaultIgnoreCondition =
                                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
                        });
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            });

        var host = await builder.StartAsync();
        return host;
    }
}

internal static class EditorWorkflowHttpContractAssertions
{
    public static void ShouldUseWorkflowDocumentInputConverter(this PropertyInfo property)
    {
        property.PropertyType.Should().Be(typeof(WorkflowDocument));
        var converterAttribute = property.GetCustomAttribute<JsonConverterAttribute>();
        converterAttribute.Should().NotBeNull();
        converterAttribute!.ConverterType.Should().NotBeNull();
        converterAttribute.ConverterType!.Name.Should().Be("EditorWorkflowDocumentJsonInputConverter");
    }
}
