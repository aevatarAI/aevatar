using System.Reflection;
using System.Text.Json;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Hosting.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Hosting.Tests;

public class ScriptCapabilityEndpointsHandlerTests
{
    private static readonly IServiceProvider HttpResultServices = new ServiceCollection()
        .AddLogging()
        .AddOptions()
        .Configure<JsonOptions>(_ => { })
        .BuildServiceProvider();

    [Fact]
    public async Task HandleProposeEvolution_ShouldReturnBadRequest_WhenScriptIdMissing()
    {
        var result = await InvokeHandleProposeEvolutionAsync(
            new ProposeScriptEvolutionHttpRequest(
                ScriptId: "",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source",
                CandidateSourceHash: "hash",
                Reason: "reason",
                ProposalId: "proposal-1"),
            new RecordingService());

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Json.GetProperty("code").GetString().Should().Be("SCRIPT_ID_REQUIRED");
    }

    [Fact]
    public async Task HandleProposeEvolution_ShouldReturnBadRequest_WhenCandidateRevisionMissing()
    {
        var result = await InvokeHandleProposeEvolutionAsync(
            new ProposeScriptEvolutionHttpRequest(
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "",
                CandidateSource: "source",
                CandidateSourceHash: "hash",
                Reason: "reason",
                ProposalId: "proposal-1"),
            new RecordingService());

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Json.GetProperty("code").GetString().Should().Be("CANDIDATE_REVISION_REQUIRED");
    }

    [Fact]
    public async Task HandleProposeEvolution_ShouldReturnBadRequest_WhenCandidateSourceMissing()
    {
        var result = await InvokeHandleProposeEvolutionAsync(
            new ProposeScriptEvolutionHttpRequest(
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "",
                CandidateSourceHash: "hash",
                Reason: "reason",
                ProposalId: "proposal-1"),
            new RecordingService());

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Json.GetProperty("code").GetString().Should().Be("CANDIDATE_SOURCE_REQUIRED");
    }

    [Fact]
    public async Task HandleProposeEvolution_ShouldMapInvalidOperationException_ToBadRequest()
    {
        var service = new RecordingService
        {
            ThrowOnPropose = new InvalidOperationException("invalid payload"),
        };

        var result = await InvokeHandleProposeEvolutionAsync(
            new ProposeScriptEvolutionHttpRequest(
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source",
                CandidateSourceHash: "hash",
                Reason: "reason",
                ProposalId: "proposal-1"),
            service);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        response.Json.GetProperty("code").GetString().Should().Be("INVALID_REQUEST");
        response.Json.GetProperty("message").GetString().Should().Contain("invalid payload");
    }

    [Fact]
    public async Task HandleProposeEvolution_ShouldReturnAccepted_AndNormalizeOptionalFields()
    {
        var service = new RecordingService();
        var result = await InvokeHandleProposeEvolutionAsync(
            new ProposeScriptEvolutionHttpRequest(
                ScriptId: "script-1",
                BaseRevision: null,
                CandidateRevision: "rev-2",
                CandidateSource: "source",
                CandidateSourceHash: null,
                Reason: null,
                ProposalId: null),
            service);

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status202Accepted);

        service.LastRequest.Should().NotBeNull();
        service.LastRequest!.ScriptId.Should().Be("script-1");
        service.LastRequest.BaseRevision.Should().BeEmpty();
        service.LastRequest.CandidateSourceHash.Should().BeEmpty();
        service.LastRequest.Reason.Should().BeEmpty();
        service.LastRequest.ProposalId.Should().BeEmpty();
        response.Json.GetProperty("proposalId").GetString().Should().Be("generated-proposal");
    }

    private static async Task<IResult> InvokeHandleProposeEvolutionAsync(
        ProposeScriptEvolutionHttpRequest request,
        IScriptEvolutionApplicationService service)
    {
        var method = typeof(ScriptCapabilityEndpoints).GetMethod(
            "HandleProposeEvolution",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();
        var task = method!.Invoke(null, [request, service, CancellationToken.None]).Should().BeAssignableTo<Task<IResult>>().Subject;
        return await task;
    }

    private static async Task<(int StatusCode, JsonElement Json)> ExecuteResultAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = HttpResultServices;
        await using var stream = new MemoryStream();
        context.Response.Body = stream;

        await result.ExecuteAsync(context);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, document.RootElement.Clone());
    }

    private sealed class RecordingService : IScriptEvolutionApplicationService
    {
        public ProposeScriptEvolutionRequest? LastRequest { get; private set; }
        public InvalidOperationException? ThrowOnPropose { get; init; }

        public Task<ScriptEvolutionCommandAccepted> ProposeAsync(
            ProposeScriptEvolutionRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastRequest = request;
            if (ThrowOnPropose != null)
                throw ThrowOnPropose;

            return Task.FromResult(
                new ScriptEvolutionCommandAccepted(
                    string.IsNullOrWhiteSpace(request.ProposalId) ? "generated-proposal" : request.ProposalId,
                    request.ScriptId,
                    "script-evolution-session:generated-proposal"));
        }
    }
}
