using System.Net;
using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Integration.Tests;

[Collection(ScopeServiceEndpointCollection.Name)]
public sealed class ScopeServiceDraftRunMultipartTests : ScopeServiceEndpointTestKit
{
    [Fact]
    public async Task ScopeDraftRunEndpoint_ShouldRejectMultipartWithoutWritingArtifacts()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/scopes/scope-a/workflow/draft-run")
        {
            Content = CreateMultipartScopeStreamContent(
                """
                {
                  "prompt": "run the draft",
                  "workflowYamls": ["name: main\nsteps:\n  - run: echo hello"]
                }
                """,
                [("cat.png", "image/png", "hello")]),
        };

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType, "body: {0}", body);
        host.WorkflowFileIngressPort.Requests.Should().BeEmpty();
        host.InteractionService.LastRequest.Should().BeNull();
    }
}
