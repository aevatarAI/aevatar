using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowHttpRequestPrimitiveTests
{
    [Fact]
    public void Parse_ShouldMapHttpRequestParametersToTypedOptions()
    {
        var workflow = new WorkflowParser().Parse("""
            name: direct-http
            steps:
              - id: fetch_snapshot
                type: http_request
                parameters:
                  method: GET
                  url: "https://api.example.com/q1000"
                  query:
                    source: q1000
                  headers:
                    X-Trace: "${input}"
                  authentication:
                    scheme: bearer
                    secret_ref: scope-secret:q1000-token
                  timeout_ms: "20000"
                  max_response_bytes: "65536"
                  max_redirects: "2"
            """);

        var step = workflow.Steps.Should().ContainSingle().Subject;
        step.Type.Should().Be("http_request");
        step.Parameters.Should().NotContainKey("connector");
        step.HttpRequestOptions.Should().NotBeNull();
        step.HttpRequestOptions!.Method.Should().Be("GET");
        step.HttpRequestOptions.Url.Should().Be("https://api.example.com/q1000");
        step.HttpRequestOptions.Query.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("source", "q1000"));
        step.HttpRequestOptions.Headers.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("X-Trace", "${input}"));
        step.HttpRequestOptions.Authentication.Should().NotBeNull();
        step.HttpRequestOptions.Authentication!.Scheme.Should().Be("bearer");
        step.HttpRequestOptions.Authentication.SecretRef.Should().Be("scope-secret:q1000-token");
        step.HttpRequestOptions.TimeoutMs.Should().Be(20000);
        step.HttpRequestOptions.MaxResponseBytes.Should().Be(65536);
        step.HttpRequestOptions.MaxRedirects.Should().Be(2);
    }

    [Theory]
    [InlineData("http_get", "GET")]
    [InlineData("http_post", "POST")]
    [InlineData("http_put", "PUT")]
    [InlineData("http_delete", "DELETE")]
    public void Parse_ShouldMapHttpAliasesToHttpRequest(string alias, string method)
    {
        var workflow = new WorkflowParser().Parse($$"""
            name: direct-http-alias
            steps:
              - id: fetch
                type: {{alias}}
                parameters:
                  url: "https://api.example.com/items"
            """);

        var step = workflow.Steps.Should().ContainSingle().Subject;
        step.Type.Should().Be("http_request");
        step.HttpRequestOptions.Should().NotBeNull();
        step.HttpRequestOptions!.Method.Should().Be(method);
        step.HttpRequestOptions.Url.Should().Be("https://api.example.com/items");
    }

    [Fact]
    public void HttpRequest_ShouldBeSideEffectingWithoutConnectorDependency()
    {
        WorkflowPrimitiveCatalog.ToCanonicalType("http_request").Should().Be("http_request");
        WorkflowPrimitiveCatalog.IsSideEffectingPrimitive("http_request").Should().BeTrue();

        var yaml = """
            name: direct-http-dependencies
            steps:
              - id: fetch
                type: http_request
                parameters:
                  method: GET
                  url: "https://api.example.com/items"
            """;

        var result = new WorkflowGAgent().EvaluateAuthorizationDependencies(yaml);

        result.Should().NotBeNull();
        result!.ConnectorCapabilityRefs.Should().BeEmpty();
        result.ServiceGrantPolicy.Should().Be(WorkflowServiceGrantPolicy.NotRequiredNoExternalService);
    }

    [Fact]
    public void WorkflowStepParameters_ShouldExposeTypedHttpRequestOptions()
    {
        WorkflowStepParameters.Descriptor.Fields.InDeclarationOrder()
            .Should()
            .Contain(field => field.Name == "http_request" && field.FieldNumber == 12);
    }
}
