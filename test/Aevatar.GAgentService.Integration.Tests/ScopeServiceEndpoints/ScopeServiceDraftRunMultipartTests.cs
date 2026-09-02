using System.Net;
using System.Net.Http.Json;
using System.Text;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;

namespace Aevatar.GAgentService.Integration.Tests;

[Collection(ScopeServiceEndpointCollection.Name)]
public sealed class ScopeServiceDraftRunMultipartTests : ScopeServiceEndpointTestKit
{
    [Fact]
    public async Task ScopeDraftRunEndpoint_ShouldIngestMultipartImageAndPassFileRefInputPart()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.InteractionService.ResultFactory = async (_, _, onAcceptedAsync, ct) =>
        {
            var receipt = new WorkflowChatRunAcceptedReceipt("run-actor-1", "main", "cmd-1", "corr-1");
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            return WorkflowChatRunInteractionResult
                .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                    WorkflowProjectionCompletionStatus.Completed,
                    true));
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/scopes/scope-a/workflow/draft-run")
        {
            Content = CreateMultipartScopeStreamContent(
                """
                {
                  "eventFormat": "agui",
                  "prompt": "run the draft",
                  "workflowYamls": ["name: main\nsteps:\n  - run: echo hello"],
                  "headers": { "channel": "draft-tests" }
                }
                """,
                [("cat.png", "image/png", "hello")]),
        };

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "body: {0}", body);
        body.Should().Contain("aevatar.run.context");
        host.WorkflowFileIngressPort.Requests.Should().ContainSingle();
        var ingressRequest = host.WorkflowFileIngressPort.Requests[0];
        ingressRequest.SourceKind.Should().Be(FileArtifactSourceKind.FormUpload);
        ingressRequest.OwnerScopeId.Should().Be("scope-a");
        ingressRequest.FileName.Should().Be("cat.png");
        ingressRequest.MediaType.Should().Be("image/png");
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.ScopeId.Should().Be("scope-a");
        host.InteractionService.LastRequest.Headers.Should().ContainKey("channel").WhoseValue.Should().Be("draft-tests");
        var part = host.InteractionService.LastRequest.InputParts.Should().ContainSingle().Which;
        part.Kind.Should().Be(WorkflowChatInputPartKind.Image);
        part.DataBase64.Should().BeNull();
        part.FileRef.Should().NotBeNull();
        part.FileRef!.ArtifactId.Should().Be("workflow-file://file-1");
        part.FileRef.SourceKind.Should().Be(FileArtifactSourceKind.FormUpload);
        part.FileRef.OwnerScopeId.Should().Be("scope-a");
    }

    [Fact]
    public async Task ScopeDraftRunEndpoint_ShouldSupportMultipartWithoutFiles()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.InteractionService.ResultFactory = async (_, _, onAcceptedAsync, ct) =>
        {
            var receipt = new WorkflowChatRunAcceptedReceipt("run-actor-1", "main", "cmd-1", "corr-1");
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            return WorkflowChatRunInteractionResult
                .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                    WorkflowProjectionCompletionStatus.Completed,
                    true));
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/scopes/scope-a/workflow/draft-run")
        {
            Content = CreateMultipartScopeStreamContent(
                """
                {
                  "prompt": "run the draft",
                  "workflowYamls": ["name: main\nsteps:\n  - run: echo hello"]
                }
                """,
                []),
        };

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "body: {0}", body);
        body.Should().Contain("aevatar.run.context");
        host.WorkflowFileIngressPort.Requests.Should().BeEmpty();
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.InputParts.Should().BeNull();
    }

    [Fact]
    public async Task ScopeDraftRunEndpoint_ShouldAppendMultipartFilesAfterPayloadInputParts()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.InteractionService.ResultFactory = async (_, _, onAcceptedAsync, ct) =>
        {
            var receipt = new WorkflowChatRunAcceptedReceipt("run-actor-1", "main", "cmd-1", "corr-1");
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            return WorkflowChatRunInteractionResult
                .Success(receipt, new CommandInteractionFinalizeResult<WorkflowProjectionCompletionStatus>(
                    WorkflowProjectionCompletionStatus.Completed,
                    true));
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/scopes/scope-a/workflow/draft-run")
        {
            Content = CreateMultipartScopeStreamContent(
                """
                {
                  "eventFormat": "agui",
                  "prompt": "compare inputs",
                  "workflowYamls": ["name: main\nsteps:\n  - run: echo hello"],
                  "inputParts": [
                    { "type": "text", "text": "payload text" }
                  ]
                }
                """,
                [("cat.png", "image/png", "hello")]),
        };

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "body: {0}", body);
        host.InteractionService.LastRequest.Should().NotBeNull();
        host.InteractionService.LastRequest!.InputParts.Should().NotBeNull();
        host.InteractionService.LastRequest.InputParts!.Should().HaveCount(2);
        host.InteractionService.LastRequest.InputParts[0].Kind.Should().Be(WorkflowChatInputPartKind.Text);
        host.InteractionService.LastRequest.InputParts[0].Text.Should().Be("payload text");
        host.InteractionService.LastRequest.InputParts[1].Kind.Should().Be(WorkflowChatInputPartKind.Image);
        host.InteractionService.LastRequest.InputParts[1].FileRef.Should().NotBeNull();
    }

    [Fact]
    public async Task ScopeDraftRunEndpoint_ShouldRejectInvalidMultipartPayload()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/scopes/scope-a/workflow/draft-run")
        {
            Content = CreateMultipartScopeStreamContent("{", []),
        };

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].Should().Be("INVALID_SCOPE_DRAFT_RUN_REQUEST");
        host.WorkflowFileIngressPort.Requests.Should().BeEmpty();
        host.InteractionService.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ScopeDraftRunEndpoint_ShouldRejectInvalidMultipartFile()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/scopes/scope-a/workflow/draft-run")
        {
            Content = CreateMultipartContentWithFileFieldName(
                """
                {
                  "prompt": "run the draft",
                  "workflowYamls": ["name: main\nsteps:\n  - run: echo hello"]
                }
                """,
                "attachment"),
        };

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotBeNull();
        body!["code"].Should().Be("INVALID_FILE_INPUT");
        host.WorkflowFileIngressPort.Requests.Should().BeEmpty();
        host.InteractionService.LastRequest.Should().BeNull();
    }

    private static MultipartFormDataContent CreateMultipartContentWithFileFieldName(
        string payloadJson,
        string fileFieldName)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(payloadJson, Encoding.UTF8, "application/json"), "payload");
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("hello"));
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content.Add(fileContent, fileFieldName, "cat.png");
        return content;
    }
}
