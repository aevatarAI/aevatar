using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
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
    public async Task HandleListAsync_ShouldAppendPairedLabelQueryParameters()
    {
        var service = new RecordingService();

        var result = await ContentArtifactEndpoints.HandleListAsync(
            CreateContext("reader-1"),
            ScopeId,
            service,
            pageSize: null,
            pageToken: null,
            teamId: null,
            kind: null,
            lifecycleStatus: null,
            workOrderId: null,
            runId: null,
            labelKey: "period",
            labelValue: "2026-08-25",
            CancellationToken.None);

        result.Should().BeOfType<Ok<ContentArtifactListResponse>>();
        service.ListQuery!.LabelKey.Should().Be("period");
        service.ListQuery.LabelValue.Should().Be("2026-08-25");
    }

    [Fact]
    public async Task HandleListAsync_ShouldMapIllegalLabelKeyTo400()
    {
        var service = new RecordingService(new ArgumentException("labelKey is invalid."));

        var result = await ContentArtifactEndpoints.HandleListAsync(
            CreateContext("reader-1"),
            ScopeId,
            service,
            pageSize: null,
            pageToken: null,
            teamId: null,
            kind: null,
            lifecycleStatus: null,
            workOrderId: null,
            runId: null,
            labelKey: "Uppercase",
            labelValue: "value",
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task PinHandlers_ShouldReturnCurrentPointerAndAcceptedMutationReceipts()
    {
        var service = new RecordingPinService();

        var get = await ContentArtifactEndpoints.HandleGetPinAsync(
            CreateContext("owner-1"), ScopeId, "daily-ops-report", service, CancellationToken.None);
        var set = await ContentArtifactEndpoints.HandleSetPinAsync(
            CreateContext("owner-1"),
            ScopeId,
            "daily-ops-report",
            new SetContentArtifactPinRequest("artifact-1", 0, "mutation-1"),
            service,
            CancellationToken.None);
        var clear = await ContentArtifactEndpoints.HandleClearPinAsync(
            CreateContext("owner-1"),
            ScopeId,
            "daily-ops-report",
            new ClearContentArtifactPinRequest(1, "mutation-2"),
            service,
            CancellationToken.None);

        get.Should().BeOfType<Ok<ContentArtifactPinCurrentStateResponse>>();
        var acceptedSet = set.Should().BeOfType<Accepted<ContentArtifactPinAcceptedReceipt>>().Subject;
        acceptedSet.Location.Should().Be(
            "/api/scopes/scope-1/content-artifact-pins/daily-ops-report");
        clear.Should().BeOfType<Accepted<ContentArtifactPinAcceptedReceipt>>();
        service.Requester.Should().Be(new ContentArtifactPrincipalContract("owner-1", "user"));
    }

    [Fact]
    public async Task HandleGetPinAsync_ShouldReturnClearedPinDocument()
    {
        var cleared = new ContentArtifactPinCurrentStateResponse(
            ScopeId,
            "daily-ops-report",
            null,
            null,
            2,
            5,
            DateTimeOffset.UnixEpoch,
            "mutation-clear",
            "succeeded",
            LastMutationRequestedBy: new ContentArtifactPrincipalContract("owner-1", "user"));
        var service = new RecordingPinService(current: cleared);

        var result = await ContentArtifactEndpoints.HandleGetPinAsync(
            CreateContext("owner-1"), ScopeId, "daily-ops-report", service, CancellationToken.None);

        var response = result.Should().BeOfType<Ok<ContentArtifactPinCurrentStateResponse>>()
            .Which.Value!;
        response.PinnedArtifactId.Should().BeNull();
        response.PinVersion.Should().Be(2);
        response.StateVersion.Should().Be(5);
        response.LastMutationStatus.Should().Be("succeeded");
    }

    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("clear")]
    public async Task PinHandlers_ShouldMapIllegalPinKeyTo400(string operation)
    {
        var service = new RecordingPinService(new ArgumentException("pinKey is invalid."));

        var result = operation switch
        {
            "get" => await ContentArtifactEndpoints.HandleGetPinAsync(
                CreateContext("owner-1"), ScopeId, "Uppercase", service, CancellationToken.None),
            "set" => await ContentArtifactEndpoints.HandleSetPinAsync(
                CreateContext("owner-1"), ScopeId, "Uppercase",
                new SetContentArtifactPinRequest("artifact-1", 0, "mutation-1"), service, CancellationToken.None),
            "clear" => await ContentArtifactEndpoints.HandleClearPinAsync(
                CreateContext("owner-1"), ScopeId, "Uppercase",
                new ClearContentArtifactPinRequest(1, "mutation-2"), service, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

        StatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
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

    [Theory]
    [InlineData("get")]
    [InlineData("get-revision")]
    [InlineData("get-current-revision")]
    [InlineData("get-content")]
    [InlineData("append")]
    [InlineData("advance")]
    [InlineData("redact")]
    [InlineData("expire")]
    [InlineData("tombstone")]
    [InlineData("attach")]
    public async Task ArtifactNotFound_ShouldMapToNonLeaking404(string operation)
    {
        var service = new RecordingService(new ContentArtifactNotFoundException(ScopeId, "artifact-1"));

        var result = await InvokeAsync(operation, service);

        StatusCode(result).Should().Be(StatusCodes.Status404NotFound);
        var body = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        body.Should().NotContain("authorized")
            .And.NotContain("tombstoned")
            .And.NotContain("concurrency");
    }

    [Fact]
    public async Task HandleCreateAsync_ShouldMapDedupKeyOccupancyTo409Only()
    {
        var service = new RecordingService(
            new ContentArtifactIdentityConflictException("report-dedup"));

        var result = await ContentArtifactEndpoints.HandleCreateAsync(
            CreateContext("owner-2"),
            ScopeId,
            CreateRequest(),
            service,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status409Conflict);
        var body = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        body.Should().Contain("CONTENT_ARTIFACT_DEDUP_KEY_OCCUPIED")
            .And.Contain("report-dedup")
            .And.NotContain("artifact-1")
            .And.NotContain("owner-1");
    }

    [Theory]
    [InlineData("get-current-revision")]
    [InlineData("get-content")]
    [InlineData("append")]
    [InlineData("attach")]
    public async Task PersistedUnavailableContent_ShouldMapUniformlyTo410(string operation)
    {
        var service = new RecordingService(new ContentArtifactContentUnavailableException(
            "artifact-1",
            "revision-1",
            ContentArtifactContentUnavailableReason.Redacted));

        var result = await InvokeAsync(operation, service);

        StatusCode(result).Should().Be(StatusCodes.Status410Gone);
    }

    [Fact]
    public async Task HandleAdvanceCurrentRevisionAsync_ShouldKeepReadableCasConflictAs400()
    {
        var service = new RecordingService(
            new InvalidOperationException("ContentArtifact concurrency version is 4, not 3."));

        var result = await InvokeAsync("advance", service);

        StatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleGetAsync_ShouldRejectRouteScopeMismatchAs403()
    {
        var service = new RecordingService();

        var result = await ContentArtifactEndpoints.HandleGetAsync(
            CreateContext("reader-1"),
            "scope-other",
            "artifact-1",
            service,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
    }

    private static Task<IResult> InvokeAsync(string operation, IContentArtifactService service) =>
        operation switch
        {
            "get" => ContentArtifactEndpoints.HandleGetAsync(
                CreateContext("reader-1"), ScopeId, "artifact-1", service, CancellationToken.None),
            "get-revision" => ContentArtifactEndpoints.HandleGetRevisionAsync(
                CreateContext("reader-1"), ScopeId, "artifact-1", "revision-1", service, CancellationToken.None),
            "get-current-revision" => ContentArtifactEndpoints.HandleGetCurrentRevisionAsync(
                CreateContext("reader-1"), ScopeId, "artifact-1", service, CancellationToken.None),
            "get-content" => ContentArtifactEndpoints.HandleGetRevisionContentAsync(
                CreateContext("reader-1"), ScopeId, "artifact-1", "revision-1", service, CancellationToken.None),
            "append" => ContentArtifactEndpoints.HandleAppendRevisionAsync(
                CreateContext("writer-1"), ScopeId, "artifact-1",
                new AppendContentArtifactRevisionRequest(CreateRequest().FirstRevision), service, CancellationToken.None),
            "advance" => ContentArtifactEndpoints.HandleAdvanceCurrentRevisionAsync(
                CreateContext("owner-1"), ScopeId, "artifact-1",
                new AdvanceContentArtifactCurrentRevisionRequest(1, "revision-1"), service, CancellationToken.None),
            "redact" => ContentArtifactEndpoints.HandleRedactRevisionAsync(
                CreateContext("owner-1"), ScopeId, "artifact-1", "revision-1",
                new RedactContentArtifactRevisionRequest(1, "privacy request"), service, CancellationToken.None),
            "expire" => ContentArtifactEndpoints.HandleExpireRevisionAsync(
                CreateContext("owner-1"), ScopeId, "artifact-1", "revision-1",
                new ExpireContentArtifactRevisionRequest(1), service, CancellationToken.None),
            "tombstone" => ContentArtifactEndpoints.HandleTombstoneAsync(
                CreateContext("owner-1"), ScopeId, "artifact-1",
                new TombstoneContentArtifactRequest(1, "retention complete"), service, CancellationToken.None),
            "attach" => ContentArtifactEndpoints.HandleAttachToRunAsync(
                CreateContext("reader-1"), ScopeId,
                new AttachContentArtifactsToRunRequest("service-1", "run-1", 1, []), service, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

    private static int? StatusCode(IResult result) =>
        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Subject.StatusCode;

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

    private sealed class RecordingService(Exception? exception = null) : IContentArtifactService
    {
        public ContentArtifactPrincipalContract? Requester { get; private set; }
        public bool ContentRead { get; private set; }
        public ContentArtifactQueryRequest? ListQuery { get; private set; }

        public Task<ContentArtifactAcceptedReceipt> CreateAsync(string scopeId, CreateContentArtifactRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default)
        {
            Requester = requester;
            ThrowIfConfigured();
            return Task.FromResult(new ContentArtifactAcceptedReceipt("artifact-1", "command-1", "correlation-1", ContentArtifactCommandStageNames.DispatchAccepted));
        }

        public Task<ContentArtifactRevisionContentResponse> GetRevisionContentAsync(string scopeId, string artifactId, string revisionId, ContentArtifactPrincipalContract requester, CancellationToken ct = default)
        {
            ContentRead = true;
            ThrowIfConfigured();
            var content = System.Text.Encoding.UTF8.GetBytes("report");
            return Task.FromResult(new ContentArtifactRevisionContentResponse(
                new ContentArtifactReferenceContract(artifactId, revisionId, Convert.ToHexStringLower(SHA256.HashData(content)), "text/markdown"),
                content));
        }

        public Task<ContentArtifactListResponse> ListAsync(string scopeId, ContentArtifactQueryRequest query, ContentArtifactPrincipalContract requester, CancellationToken ct = default)
        {
            ListQuery = query;
            Requester = requester;
            return Task.FromResult(Result(new ContentArtifactListResponse(scopeId, [])));
        }
        public Task<ContentArtifactCurrentStateResponse> GetAsync(string scopeId, string artifactId, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => Task.FromResult(Result(Current()));
        public Task<ContentArtifactRevisionResponse> GetRevisionAsync(string scopeId, string artifactId, string revisionId, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => Task.FromResult(Result(Revision()));
        public Task<ContentArtifactRevisionResponse> GetCurrentRevisionAsync(string scopeId, string artifactId, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => Task.FromResult(Result(Revision()));
        public Task<ContentArtifactAcceptedReceipt> AppendRevisionAsync(string scopeId, string artifactId, AppendContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => Task.FromResult(Result(Receipt()));
        public Task<ContentArtifactAcceptedReceipt> AdvanceCurrentRevisionAsync(string scopeId, string artifactId, AdvanceContentArtifactCurrentRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => Task.FromResult(Result(Receipt()));
        public Task<ContentArtifactAcceptedReceipt> RedactRevisionAsync(string scopeId, string artifactId, string revisionId, RedactContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => Task.FromResult(Result(Receipt()));
        public Task<ContentArtifactAcceptedReceipt> ExpireRevisionAsync(string scopeId, string artifactId, string revisionId, ExpireContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => Task.FromResult(Result(Receipt()));
        public Task<ContentArtifactAcceptedReceipt> TombstoneAsync(string scopeId, string artifactId, TombstoneContentArtifactRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => Task.FromResult(Result(Receipt()));
        public Task<ContentArtifactRunAttachmentReceipt> AttachToRunAsync(string scopeId, AttachContentArtifactsToRunRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default) => Task.FromResult(Result(new ContentArtifactRunAttachmentReceipt("run-1", "command-1", "correlation-1", ContentArtifactCommandStageNames.DispatchAccepted)));

        private T Result<T>(T value)
        {
            ThrowIfConfigured();
            return value;
        }

        private void ThrowIfConfigured()
        {
            if (exception != null)
                throw exception;
        }

        private static ContentArtifactAcceptedReceipt Receipt() =>
            new("artifact-1", "command-1", "correlation-1", ContentArtifactCommandStageNames.DispatchAccepted);

        private static ContentArtifactRevisionResponse Revision() =>
            new("revision-1", 1, null, "text/markdown", 6, new string('a', 64),
                ContentArtifactRevisionAvailabilityNames.Available, true, false,
                new ContentArtifactExecutionProvenanceContract(ScopeId), [], DateTimeOffset.UtcNow);

        private static ContentArtifactCurrentStateResponse Current() =>
            new("artifact-1", ScopeId, null, "markdown", "Report", "internal",
                ContentArtifactLifecycleStatusNames.Active, "revision-1", 1, 1,
                new ContentArtifactPrincipalContract("owner-1", "user"), [], [], null, null,
                [Revision()], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private sealed class RecordingPinService(
        Exception? exception = null,
        ContentArtifactPinCurrentStateResponse? current = null) : IContentArtifactPinService
    {
        public ContentArtifactPrincipalContract? Requester { get; private set; }

        public Task<ContentArtifactPinCurrentStateResponse> GetAsync(
            string scopeId,
            string pinKey,
            CancellationToken ct = default) =>
            Task.FromResult(Result(current ?? new ContentArtifactPinCurrentStateResponse(
                scopeId,
                pinKey,
                "artifact-1",
                new ContentArtifactPrincipalContract("owner-1", "user"),
                1,
                1,
                DateTimeOffset.UnixEpoch,
                "mutation-1",
                "succeeded")));

        public Task<ContentArtifactPinAcceptedReceipt> SetAsync(
            string scopeId,
            string pinKey,
            SetContentArtifactPinRequest request,
            ContentArtifactPrincipalContract requester,
            CancellationToken ct = default)
        {
            Requester = requester;
            return Task.FromResult(Result(Receipt(scopeId, pinKey)));
        }

        public Task<ContentArtifactPinAcceptedReceipt> ClearAsync(
            string scopeId,
            string pinKey,
            ClearContentArtifactPinRequest request,
            ContentArtifactPrincipalContract requester,
            CancellationToken ct = default)
        {
            Requester = requester;
            return Task.FromResult(Result(Receipt(scopeId, pinKey)));
        }

        private T Result<T>(T value)
        {
            if (exception != null)
                throw exception;
            return value;
        }

        private static ContentArtifactPinAcceptedReceipt Receipt(string scopeId, string pinKey) =>
            new(
                scopeId,
                pinKey,
                "command-1",
                "correlation-1",
                ContentArtifactCommandStageNames.DispatchAccepted);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Aevatar.Studio.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
