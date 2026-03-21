using System.Text;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScopeScriptEndpointsTests
{
    [Fact]
    public async Task HandleUpsertScriptAsync_ShouldReturnBadRequest_WhenServiceRejectsRequest()
    {
        var result = await ScopeScriptEndpoints.HandleUpsertScriptAsync(
            "user-1",
            "script-1",
            new ScopeScriptEndpoints.UpsertScopeScriptHttpRequest(string.Empty),
            new RejectingScopeScriptCommandPort(),
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("INVALID_SCOPE_SCRIPT_REQUEST");
        body.Should().Contain("SourceText is required");
    }

    [Fact]
    public async Task HandleListScriptsAsync_ShouldIncludeSource_WhenRequested()
    {
        var updatedAt = DateTimeOffset.UtcNow;
        var queryPort = new RecordingScopeScriptQueryPort
        {
            ListResult =
            [
                new ScopeScriptSummary(
                    "user-1",
                    "script-1",
                    "catalog-1",
                    "definition-1",
                    "rev-1",
                    "hash-1",
                    updatedAt),
            ],
        };
        var snapshotPort = new RecordingDefinitionSnapshotPort
        {
            Snapshot = new ScriptDefinitionSnapshot(
                "script-1",
                "rev-1",
                "public sealed class DemoScript {}",
                "hash-1",
                StateTypeUrl: string.Empty,
                ReadModelTypeUrl: string.Empty,
                ReadModelSchemaVersion: string.Empty,
                ReadModelSchemaHash: string.Empty,
                DefinitionActorId: "definition-1",
                ScopeId: "user-1"),
        };

        var result = await ScopeScriptEndpoints.HandleListScriptsAsync(
            "user-1",
            includeSource: true,
            queryPort,
            snapshotPort,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("\"scopeId\":\"user-1\"");
        body.Should().Contain("\"scriptId\":\"script-1\"");
        body.Should().Contain("\"sourceText\":\"public sealed class DemoScript {}\"");
        snapshotPort.LastRequest.Should().BeEquivalentTo(
            new RecordingDefinitionSnapshotPort.Request("definition-1", "rev-1"));
    }

    [Fact]
    public async Task HandleGetScriptDetailAsync_ShouldReturnNotFound_WhenScriptMissing()
    {
        var result = await ScopeScriptEndpoints.HandleGetScriptDetailAsync(
            "user-1",
            "missing-script",
            new RecordingScopeScriptQueryPort(),
            new RecordingDefinitionSnapshotPort(),
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        body.Should().Contain("SCOPE_SCRIPT_NOT_FOUND");
        body.Should().Contain("missing-script");
    }

    [Fact]
    public async Task HandleProposeScriptEvolutionAsync_ShouldBindScopeAndScriptId()
    {
        var service = new RecordingScriptEvolutionApplicationService();

        var result = await ScopeScriptEndpoints.HandleProposeScriptEvolutionAsync(
            "user-1",
            "script-1",
            new ScopeScriptEndpoints.ProposeScopeScriptEvolutionHttpRequest(
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "public sealed class DemoScriptV2 {}",
                CandidateSourceHash: "hash-2",
                Reason: "rollout",
                ProposalId: "proposal-1"),
            service,
            CancellationToken.None);

        var http = CreateHttpContext();
        await result.ExecuteAsync(http);
        var body = await ReadBodyAsync(http.Response);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("\"proposalId\":\"proposal-1\"");
        service.LastRequest.Should().BeEquivalentTo(
            new ProposeScriptEvolutionRequest(
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "public sealed class DemoScriptV2 {}",
                CandidateSourceHash: "hash-2",
                Reason: "rollout",
                ProposalId: "proposal-1",
                ScopeId: "user-1"));
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddOptions()
                .BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static async Task<string> ReadBodyAsync(HttpResponse response)
    {
        response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class RejectingScopeScriptCommandPort : IScopeScriptCommandPort
    {
        public Task<ScopeScriptUpsertResult> UpsertAsync(ScopeScriptUpsertRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var normalized = request.SourceText?.Trim() ?? string.Empty;
            if (normalized.Length == 0)
                throw new InvalidOperationException("SourceText is required.");

            throw new NotSupportedException();
        }
    }

    private sealed class RecordingScopeScriptQueryPort : IScopeScriptQueryPort
    {
        public IReadOnlyList<ScopeScriptSummary> ListResult { get; set; } = [];

        public ScopeScriptSummary? GetResult { get; set; }

        public Task<IReadOnlyList<ScopeScriptSummary>> ListAsync(string scopeId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ListResult);
        }

        public Task<ScopeScriptSummary?> GetByScriptIdAsync(string scopeId, string scriptId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(GetResult);
        }
    }

    private sealed class RecordingDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
    {
        public ScriptDefinitionSnapshot? Snapshot { get; set; }

        public Request? LastRequest { get; private set; }

        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(string definitionActorId, string requestedRevision, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastRequest = new Request(definitionActorId, requestedRevision);
            return Snapshot == null
                ? throw new InvalidOperationException("snapshot not found")
                : Task.FromResult(Snapshot);
        }

        public sealed record Request(string DefinitionActorId, string RequestedRevision);
    }

    private sealed class RecordingScriptEvolutionApplicationService : IScriptEvolutionApplicationService
    {
        public ProposeScriptEvolutionRequest? LastRequest { get; private set; }

        public Task<ScriptPromotionDecision> ProposeAsync(ProposeScriptEvolutionRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult(new ScriptPromotionDecision(
                Accepted: true,
                ProposalId: request.ProposalId,
                ScriptId: request.ScriptId,
                BaseRevision: request.BaseRevision,
                CandidateRevision: request.CandidateRevision,
                Status: ScriptEvolutionStatuses.Promoted,
                FailureReason: string.Empty,
                DefinitionActorId: "definition-1",
                CatalogActorId: "catalog-1",
                ValidationReport: ScriptEvolutionValidationReport.Empty));
        }
    }
}
