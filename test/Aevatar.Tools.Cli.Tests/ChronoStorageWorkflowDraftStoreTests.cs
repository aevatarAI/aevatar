using System.Net;
using Aevatar.Studio.Application.Protos;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Domain.Studio.Models;
using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ChronoStorageWorkflowDraftStoreTests
{
    [Fact]
    public async Task SaveDraftAsync_ShouldUploadProtobufDraftFact()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var store = CreateStore(handler);

        await store.SaveDraftAsync(
            "scope-1",
            " workflow-1 ",
            "workflow-one",
            "name: workflow-one\nsteps: []\n",
            NewLayout(),
            CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri.Should().Contain("key=scope-1%2Fworkflows%2Fworkflow-1.yaml");
        request.RequestUri.Should().Contain("contentType=application%2Fx-protobuf");
        request.ContentType.Should().Be("application/x-protobuf");

        var fact = ScopedWorkflowDraftFact.Parser.ParseFrom(request.Body);
        fact.WorkflowId.Should().Be("workflow-1");
        fact.WorkflowName.Should().Be("workflow-one");
        fact.Yaml.Should().Contain("workflow-one");
        fact.Layout.EntryWorkflow.Should().Be("workflow-one");
        fact.Layout.Nodes.Should().ContainSingle().Which.NodeId.Should().Be("start");
        fact.Layout.Groups.Should().ContainSingle().Which.NodeIds.Should().Equal("start");
        fact.Layout.Collapsed.Should().Equal("group-1");
        fact.Layout.Viewport.Zoom.Should().Be(0.75);
    }

    [Fact]
    public async Task GetDraftAsync_ShouldReturnDraftFromStoredProtobufPayload()
    {
        var fact = new ScopedWorkflowDraftFact
        {
            WorkflowId = "workflow-1",
            WorkflowName = "workflow-one",
            Yaml = "name: workflow-one\nsteps: []\n",
            Layout = new ScopedWorkflowLayoutFact
            {
                EntryWorkflow = "workflow-one",
                Viewport = new ScopedWorkflowViewportFact { X = 1, Y = 2, Zoom = 0.75 },
                Nodes = { new ScopedWorkflowNodeLayoutFact { NodeId = "start", X = 10, Y = 20 } },
                Groups =
                {
                    new ScopedWorkflowLayoutGroupFact
                    {
                        GroupId = "group-1",
                        NodeIds = { "start" },
                    },
                },
                Collapsed = { "group-1" },
            },
        };
        var handler = new RecordingHttpMessageHandler(request =>
        {
            request.RequestUri!.ToString().Should().Contain("objects/download");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(fact.ToByteArray()),
            };
        });
        var store = CreateStore(handler);

        var draft = await store.GetDraftAsync("scope-1", "workflow-1", CancellationToken.None);

        draft.Should().NotBeNull();
        draft!.WorkflowId.Should().Be("workflow-1");
        draft.WorkflowName.Should().Be("workflow-one");
        draft.Yaml.Should().Be("name: workflow-one\nsteps: []\n");
        draft.Layout.Should().NotBeNull();
        draft.Layout!.EntryWorkflow.Should().Be("workflow-one");
        draft.Layout.NodePositions["start"].Y.Should().Be(20);
        draft.Layout.Groups["group-1"].Should().Equal("start");
        draft.Layout.Collapsed.Should().Equal("group-1");
        draft.Layout.Viewport.Zoom.Should().Be(0.75);
    }

    private static WorkflowLayoutDocument NewLayout() => new()
    {
        EntryWorkflow = "workflow-one",
        Viewport = new WorkflowViewport(1, 2, 0.75),
        NodePositions = new Dictionary<string, WorkflowNodeLayout>(StringComparer.Ordinal)
        {
            ["start"] = new(10, 20),
        },
        Groups = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["group-1"] = ["start"],
        },
        Collapsed = ["group-1"],
    };

    private static ChronoStorageWorkflowDraftStore CreateStore(HttpMessageHandler handler)
    {
        var blobClient = new ChronoStorageCatalogBlobClient(
            new StubScopeResolver(),
            new StubHttpClientFactory(new HttpClient(handler)),
            Options.Create(new ConnectorCatalogStorageOptions
            {
                Enabled = true,
                UseNyxProxy = false,
                BaseUrl = "https://chrono.example.com",
                Bucket = "test-bucket",
            }));
        return new ChronoStorageWorkflowDraftStore(blobClient);
    }

    private sealed class StubScopeResolver : IAppScopeResolver
    {
        public AppScopeContext? Resolve(HttpContext? httpContext = null) => new("scope-1", "test");
        public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null) => false;
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                body));
            return responseFactory(request);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string? RequestUri,
        string? ContentType,
        byte[] Body);
}
