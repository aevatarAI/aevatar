using System.Net;
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
    public async Task SerializeYaml_ShouldAcceptPlainJsonStepParameters()
    {
        using var host = await StartHostAsync();
        var client = host.GetTestClient();

        using var response = await client.PostAsJsonAsync("/api/editor/serialize-yaml", BuildPlainParameterRequest());

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("target: result");
        body.Should().Contain("value: $input");
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
