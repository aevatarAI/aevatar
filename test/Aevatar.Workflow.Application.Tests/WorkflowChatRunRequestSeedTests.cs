using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowChatRunRequestSeedTests
{
    [Fact]
    public void WorkflowChatRunRequest_ShouldExposeContextSeedsAndHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["trace"] = "trace-1",
        };
        var request = new WorkflowChatRunRequest(
            Prompt: "hello",
            Source: WorkflowChatSource.CatalogWorkflow("direct"),
            Headers: headers,
            CommandIdSeed: "cmd-1",
            CorrelationIdSeed: "corr-1");

        var seed = request.Should().BeAssignableTo<ICommandContextSeed>().Subject;

        seed.CommandId.Should().Be("cmd-1");
        seed.CorrelationId.Should().Be("corr-1");
        seed.Headers.Should().BeSameAs(headers);
    }

    [Fact]
    public void WorkflowChatRunRequest_ShouldNotSerializeTargetSeed()
    {
        var request = new WorkflowChatRunRequest(
            Prompt: "hello",
            Source: WorkflowChatSource.CatalogWorkflow("direct"),
            TargetSeed: new WorkflowRunTargetSeed(
                ActorId: "run-1",
                WorkflowNameForRun: "direct",
                CreatedActorIds: ["definition-1", "run-1"],
                Source: WorkflowChatSource.CatalogWorkflow("direct")));

        var json = JsonSerializer.Serialize(request);

        json.Should().NotContain("TargetSeed");
        json.Should().NotContain("run-1");
    }

    [Fact]
    public void WorkflowChatRequestEnvelopeFactory_ShouldMapExternalIngressAsTypedProto()
    {
        var factory = new WorkflowChatRequestEnvelopeFactory();
        var request = new WorkflowChatRunRequest(
            Prompt: "hello",
            Source: WorkflowChatSource.CatalogWorkflow("direct"),
            ExternalIngress: new Aevatar.Workflow.Application.Abstractions.Runs.WorkflowExternalIngressContext(
                RouteKey: "invoice",
                SourceId: "lark",
                DeliveryId: "delivery-1",
                ReceivedAtUnixMs: 1710000000000,
                ContentType: "application/json",
                PayloadFingerprint: "abc",
                AuthScheme: "hmac-sha256",
                PrincipalSubject: "lark"));

        var envelope = factory.CreateEnvelope(
            request,
            new CommandContext(
                "cmd-1",
                "corr-1",
                "target-1",
                new Dictionary<string, string>(StringComparer.Ordinal)));
        var payload = envelope.Payload.Unpack<WorkflowChatRequestEvent>();

        payload.ExternalIngress.RouteKey.Should().Be("invoice");
        payload.ExternalIngress.SourceId.Should().Be("lark");
        payload.ExternalIngress.DeliveryId.Should().Be("delivery-1");
        payload.ExternalIngress.ReceivedAtUnixMs.Should().Be(1710000000000);
        payload.ExternalIngress.ContentType.Should().Be("application/json");
        payload.ExternalIngress.PayloadFingerprint.Should().Be("abc");
        payload.ExternalIngress.AuthScheme.Should().Be("hmac-sha256");
        payload.ExternalIngress.PrincipalSubject.Should().Be("lark");
        payload.Metadata.Should().NotContainKey("external_ingress");
    }
}
