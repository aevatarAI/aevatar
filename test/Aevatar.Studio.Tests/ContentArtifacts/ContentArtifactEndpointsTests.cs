using System.Security.Claims;
using System.Security.Cryptography;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Tests.ContentArtifacts;

public sealed class ContentArtifactEndpointsTests
{
    private const string ScopeId = "scope-1";

    [Fact]
    public async Task HandleCreateAsync_ShouldReturnAcceptedReceiptForAuthenticatedOwner()
    {
        var service = new RecordingService();
        var request = CreateRequest();

        var result = await ContentArtifactEndpoints.HandleCreateAsync(
            CreateContext("owner-1"),
            ScopeId,
            request,
            service,
            CancellationToken.None);

        var accepted = result.Should().BeOfType<Accepted<ContentArtifactAcceptedReceipt>>().Subject;
        accepted.Location.Should().Be($"/api/scopes/{ScopeId}/content-artifacts/artifact-1");
        accepted.Value!.Stage.Should().Be(ContentArtifactCommandStageNames.DispatchAccepted);
        service.Requester.Should().Be(new ContentArtifactPrincipalContract("owner-1", "user"));
    }

    [Fact]
    public async Task HandleGetRevisionContentAsync_ShouldRequireAuthenticatedPrincipal()
    {
        var service = new RecordingService();

        var result = await ContentArtifactEndpoints.HandleGetRevisionContentAsync(
            CreateContext(requesterId: null),
            ScopeId,
            "artifact-1",
            "revision-1",
            service,
            CancellationToken.None);

        result.Should().BeOfType<UnauthorizedHttpResult>();
        service.ContentRead.Should().BeFalse();
    }

    [Fact]
    public async Task HandleGetRevisionContentAsync_ShouldReturnVerifiedContentWithMediaType()
    {
        var service = new RecordingService();

        var result = await ContentArtifactEndpoints.HandleGetRevisionContentAsync(
            CreateContext("reader-1"),
            ScopeId,
            "artifact-1",
            "revision-1",
            service,
            CancellationToken.None);

        var file = result.Should().BeOfType<FileContentHttpResult>().Subject;
        file.ContentType.Should().Be("text/markdown");
        file.FileContents.Should().BeOfType<ReadOnlyMemory<byte>>().Which.ToArray()
            .Should().Equal(System.Text.Encoding.UTF8.GetBytes("report"));
    }

    private static CreateContentArtifactRequest CreateRequest()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("report");
        return new CreateContentArtifactRequest(
            "team-1",
            "markdown",
            "Quarterly report",
            "internal",
            "report-dedup",
            new ContentArtifactRevisionWriteRequest(
                "revision-1-dedup",
                "text/markdown",
                Convert.ToHexStringLower(SHA256.HashData(content)),
                content.Length,
                new ContentArtifactExecutionProvenanceContract(ScopeId, TeamId: "team-1"),
                InlineContent: content));
    }

    private static HttpContext CreateContext(string? requesterId)
    {
        var claims = new List<Claim> { new("scope_id", ScopeId) };
        if (requesterId != null)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, requesterId));
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "true",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .BuildServiceProvider();
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
            RequestServices = services,
        };
    }

    private sealed class RecordingService : IContentArtifactService
    {
        public ContentArtifactPrincipalContract? Requester { get; private set; }
        public bool ContentRead { get; private set; }

        public Task<ContentArtifactAcceptedReceipt> CreateAsync(string scopeId, CreateContentArtifactRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default)
        {
            Requester = requester;
            return Task.FromResult(new ContentArtifactAcceptedReceipt("artifact-1", "command-1", "correlation-1", ContentArtifactCommandStageNames.DispatchAccepted));
        }

        public Task<ContentArtifactRevisionContentResponse> GetRevisionContentAsync(string scopeId, string artifactId, string revisionId, ContentArtifactPrincipalContract requester, CancellationToken ct = default)
        {
            ContentRead = true;
            var content = System.Text.Encoding.UTF8.GetBytes("report");
            return Task.FromResult(new ContentArtifactRevisionContentResponse(
                new ContentArtifactReferenceContract(artifactId, revisionId, Convert.ToHexStringLower(SHA256.HashData(content)), "text/markdown"),
                content));
        }

        public Task<ContentArtifactListResponse> ListAsync(string scopeId, ContentArtifactQueryRequest query, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactCurrentStateResponse> GetAsync(string scopeId, string artifactId, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactRevisionResponse> GetRevisionAsync(string scopeId, string artifactId, string revisionId, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactRevisionResponse> GetCurrentRevisionAsync(string scopeId, string artifactId, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactAcceptedReceipt> AppendRevisionAsync(string scopeId, string artifactId, AppendContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactAcceptedReceipt> AdvanceCurrentRevisionAsync(string scopeId, string artifactId, AdvanceContentArtifactCurrentRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactAcceptedReceipt> RedactRevisionAsync(string scopeId, string artifactId, string revisionId, RedactContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactAcceptedReceipt> ExpireRevisionAsync(string scopeId, string artifactId, string revisionId, ExpireContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactAcceptedReceipt> TombstoneAsync(string scopeId, string artifactId, TombstoneContentArtifactRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ContentArtifactRunAttachmentReceipt> AttachToRunAsync(string scopeId, AttachContentArtifactsToRunRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Aevatar.Studio.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
