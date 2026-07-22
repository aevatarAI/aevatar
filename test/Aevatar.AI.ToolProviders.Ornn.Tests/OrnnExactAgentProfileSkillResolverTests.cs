using System.Net;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn.AgentProfiles;
using Aevatar.AI.ToolProviders.Ornn.Publishing;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnExactAgentProfileSkillResolverTests
{
    private const string SkillGuid = "2d05bf2e-88ee-4f76-9998-728ba2f9db10";
    private const string LiteralVersion = "1.4";
    private const string SkillName = "xiaomi-home-control";
    private const string PublisherId = "publisher-alpha";

    [Fact]
    public async Task ResolveAsync_ShouldReadOnlyLiteralVersionPinnedGuidEndpointsAndMapTypedPackage()
    {
        var handler = SuccessHandler(files: new Dictionary<string, string>
        {
            ["SKILL.md"] = SkillMarkdown(),
            ["references/zeta.txt"] = "Zeta\r\nreference",
            ["references/alpha.txt"] = "Alpha reference",
            ["assets/zeta.txt"] = "Zeta asset",
            ["assets/alpha.txt"] = "Alpha asset",
        });
        var resolver = CreateResolver(handler);

        var result = await resolver.ResolveAsync("access-token", ExactReference());

        result.IsSuccess.Should().BeTrue();
        result.Package.Should().NotBeNull();
        var package = result.Package!;
        package.SkillGuid.Should().Be(SkillGuid);
        package.LiteralVersion.Should().Be(LiteralVersion);
        package.CanonicalName.Should().Be(SkillName);
        package.PublisherId.Should().Be(PublisherId);
        package.UpstreamSkillHash.Should().Be("hash-alpha");
        package.Description.Should().Be("Controls a home");
        package.Instructions.Should().Be("Inspect state.\nApply requested change.");
        package.Arguments.Should().Be("device and action");
        package.WhenToUse.Should().Be("Use for exact home control");
        package.ModelInvocable.Should().BeTrue();
        package.UserInvocable.Should().BeTrue();
        package.DeclaredToolNames.Should().Equal("tool-alpha", "tool-zeta");
        package.References.Select(static asset => asset.Path).Should().Equal(
            "references/alpha.txt",
            "references/zeta.txt");
        package.Assets.Select(static asset => asset.Path).Should().Equal(
            "assets/alpha.txt",
            "assets/zeta.txt");
        package.References[1].Content.Should().Be("Zeta\nreference");

        handler.Requests.Select(static request => request.RequestUri!.AbsoluteUri).Should().Equal(
            $"https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/{SkillGuid}?version=1.4",
            $"https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/{SkillGuid}/json?version=1.4");
        handler.Requests.Should().OnlyContain(request =>
            request.Method == HttpMethod.Get &&
            request.Authorization != null &&
            request.Authorization.Scheme == "Bearer" &&
            request.Authorization.Parameter == "access-token");
    }

    [Fact]
    public async Task ResolveAsync_ShouldRejectNonCanonicalReferenceAndMissingBearerBeforeHttp()
    {
        var handler = SuccessHandler();
        var resolver = CreateResolver(handler);

        var missingBearer = await resolver.ResolveAsync(" ", ExactReference());
        var uppercaseGuid = ExactReference();
        uppercaseGuid.SkillGuid = SkillGuid.ToUpperInvariant();
        var invalidGuid = await resolver.ResolveAsync("token", uppercaseGuid);
        var latest = ExactReference();
        latest.LiteralVersion = "latest";
        var invalidVersion = await resolver.ResolveAsync("token", latest);

        missingBearer.Failure.Should().NotBeNull();
        missingBearer.Failure!.Code.Should().Be("ORNN_ACCESS_TOKEN_REQUIRED");
        invalidGuid.Failure!.Code.Should().Be("INVALID_SKILL_GUID");
        invalidVersion.Failure!.Code.Should().Be("INVALID_LITERAL_VERSION");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, HttpStatusCode.Forbidden, "ORNN_SKILL_ACCESS_DENIED", 1)]
    [InlineData(false, HttpStatusCode.Forbidden, "ORNN_SKILL_ACCESS_DENIED", 2)]
    [InlineData(true, HttpStatusCode.NotFound, "ORNN_SKILL_NOT_FOUND", 1)]
    [InlineData(false, HttpStatusCode.NotFound, "ORNN_SKILL_NOT_FOUND", 2)]
    public async Task ResolveAsync_ShouldMapExactEndpointFailuresWithoutFallback(
        bool failDetail,
        HttpStatusCode status,
        string expectedCode,
        int expectedRequestCount)
    {
        const string remoteSecret = "raw-upstream-secret-body";
        var handler = failDetail
            ? new OrnnTestHttpMessageHandler(
                _ => OrnnTestHttpMessageHandler.JsonResponse(
                    $$"""{"error":"{{remoteSecret}}"}""",
                    status))
            : new OrnnTestHttpMessageHandler(
                _ => OrnnTestHttpMessageHandler.JsonResponse(DetailEnvelope()),
                _ => OrnnTestHttpMessageHandler.JsonResponse(
                    $$"""{"error":"{{remoteSecret}}"}""",
                    status));

        var result = await CreateResolver(handler).ResolveAsync("token", ExactReference());

        result.IsSuccess.Should().BeFalse();
        result.Failure!.Code.Should().Be(expectedCode);
        result.Failure.Message.Should().NotContain(remoteSecret);
        result.Failure.Path.Should().NotContain(remoteSecret);
        handler.Requests.Should().HaveCount(expectedRequestCount);
        handler.Requests.Should().OnlyContain(request =>
            request.RequestUri!.Query == "?version=1.4" &&
            request.RequestUri.AbsolutePath.Contains($"/skills/{SkillGuid}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveAsync_InternalTimeout_ShouldReturnStableDependencyFailure()
    {
        var handler = new CancellationObservingHttpMessageHandler();
        var timeProvider = new FakeTimeProvider();
        var resolver = CreateResolver(
            handler,
            perCallTimeout: TimeSpan.FromSeconds(1),
            timeProvider: timeProvider);

        var pending = resolver.ResolveAsync("token", ExactReference());
        await handler.Started;
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var result = await pending;

        result.IsSuccess.Should().BeFalse();
        result.Failure!.Code.Should().Be("ORNN_DEPENDENCY_UNAVAILABLE");
        handler.CancellationObserved.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_CallerCancellation_ShouldPropagate()
    {
        var handler = OrnnTestHttpMessageHandler.HangingUntilCanceled();
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();

        var act = async () => await CreateResolver(handler)
            .ResolveAsync("token", ExactReference(), callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    public async Task ResolveAsync_InvalidOrNullRemoteEnvelope_ShouldReturnStableRedactedFailure(string body)
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson(body);

        var result = await CreateResolver(handler).ResolveAsync("token", ExactReference());

        result.IsSuccess.Should().BeFalse();
        result.Failure!.Code.Should().Be("ORNN_DEPENDENCY_UNAVAILABLE");
        result.Failure.Message.Should().NotContain(body);
        handler.Requests.Should().ContainSingle();
    }

    public static TheoryData<string, string, string, string> IdentityMismatchCases => new()
    {
        {
            "detail guid mismatch",
            DetailEnvelope(guid: "bdde6370-66cf-47db-b55f-267463c51df0"),
            JsonEnvelope(),
            "ORNN_SKILL_IDENTITY_MISMATCH"
        },
        {
            "json version mismatch",
            DetailEnvelope(),
            JsonEnvelope(version: "1.5"),
            "ORNN_SKILL_IDENTITY_MISMATCH"
        },
        {
            "detail and json name mismatch",
            DetailEnvelope(name: SkillName),
            JsonEnvelope(name: "other-skill"),
            "ORNN_SKILL_IDENTITY_MISMATCH"
        },
        {
            "expected name mismatch",
            DetailEnvelope(name: "other-skill"),
            JsonEnvelope(name: "other-skill"),
            "ORNN_SKILL_IDENTITY_MISMATCH"
        },
        {
            "expected publisher mismatch",
            DetailEnvelope(publisher: "publisher-beta"),
            JsonEnvelope(),
            "ORNN_SKILL_PUBLISHER_MISMATCH"
        },
        {
            "missing skill hash",
            DetailEnvelope(hash: ""),
            JsonEnvelope(),
            "INVALID_SKILL_PACKAGE"
        },
    };

    [Theory]
    [MemberData(nameof(IdentityMismatchCases))]
    public async Task ResolveAsync_ShouldFailClosedOnIdentityOrIntegrityMismatch(
        string _,
        string detail,
        string json,
        string expectedCode)
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(detail),
            _ => OrnnTestHttpMessageHandler.JsonResponse(json));

        var result = await CreateResolver(handler).ResolveAsync("token", ExactReference());

        result.IsSuccess.Should().BeFalse();
        result.Failure!.Code.Should().Be(expectedCode);
        handler.Requests.Should().HaveCount(2);
    }

    public static TheoryData<IReadOnlyDictionary<string, string>> InvalidPackageCases => new()
    {
        new Dictionary<string, string> { ["README.md"] = "missing skill markdown" },
        new Dictionary<string, string>
        {
            ["SKILL.md"] = SkillMarkdown(),
            ["skill.md"] = SkillMarkdown(),
        },
        new Dictionary<string, string>
        {
            ["SKILL.md"] = SkillMarkdown(),
            ["assets/../credential.txt"] = "secret",
        },
        new Dictionary<string, string>
        {
            ["SKILL.md"] = SkillMarkdown(),
            ["assets\\same.txt"] = "one",
            ["assets/same.txt"] = "two",
        },
    };

    [Theory]
    [MemberData(nameof(InvalidPackageCases))]
    public async Task ResolveAsync_ShouldRejectMissingDuplicateOrUnsafePackagePaths(
        IReadOnlyDictionary<string, string> files)
    {
        var handler = SuccessHandler(files: files);

        var result = await CreateResolver(handler).ResolveAsync("token", ExactReference());

        result.IsSuccess.Should().BeFalse();
        result.Failure!.Code.Should().Be("INVALID_SKILL_PACKAGE");
        result.Failure.Message.Should().Be("Exact Ornn skill package is invalid.");
    }

    [Theory]
    [InlineData(OrnnSkillPublishValidationPipeline.WorkflowYamlAssetKind, "workflows/route.yaml", "INVALID_WORKFLOW_SECRET")]
    [InlineData(OrnnSkillPublishValidationPipeline.ScriptAssetKind, "scripts/Main.cs", "INVALID_SCRIPT_SECRET")]
    public async Task ResolveAsync_ShouldRunRegisteredAssetValidatorsAndRedactTheirDetails(
        string assetKind,
        string assetPath,
        string rawMarker)
    {
        var files = new Dictionary<string, string>
        {
            ["SKILL.md"] = SkillMarkdown(scriptEntry: "Example.EntryBehavior"),
            [assetPath] = assetKind == OrnnSkillPublishValidationPipeline.WorkflowYamlAssetKind
                ? $"name: route\nsteps: [{rawMarker}]"
                : $"public sealed class Main {{ string value = \"{rawMarker}\"; }}",
        };
        var pipeline = new OrnnSkillPublishValidationPipeline(
        [
            new MarkerAssetValidator(
                OrnnSkillPublishValidationPipeline.WorkflowYamlAssetKind,
                "INVALID_WORKFLOW_SECRET"),
            new MarkerAssetValidator(
                OrnnSkillPublishValidationPipeline.ScriptAssetKind,
                "INVALID_SCRIPT_SECRET"),
        ]);

        var result = await CreateResolver(SuccessHandler(files: files), pipeline)
            .ResolveAsync("token", ExactReference());

        result.IsSuccess.Should().BeFalse();
        result.Failure!.Code.Should().Be("INVALID_SKILL_PACKAGE");
        result.Failure.Message.Should().NotContain(rawMarker);
        result.Failure.Path.Should().Be(assetPath);
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotUseNameCapableRemoteFetcher()
    {
        var handler = SuccessHandler();
        var trackingFetcher = new TrackingRemoteSkillFetcher();
        var services = new ServiceCollection();
        services.AddSingleton<IRemoteSkillFetcher>(trackingFetcher);
        services.AddSingleton(new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler)));
        services.AddOrnnSkills(options => options.NyxIdSlug = "ornn");

        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IExactOrnnSkillResolver>()
            .ResolveAsync("token", ExactReference());

        result.IsSuccess.Should().BeTrue();
        trackingFetcher.Calls.Should().Be(0);
        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().NotContain(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/json", StringComparison.Ordinal) &&
            request.RequestUri.Query.Length == 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddOrnnSkills_ShouldReplaceUnavailableExactResolverRegardlessOfTryAddOrder(bool fallbackFirst)
    {
        var services = new ServiceCollection();
        if (fallbackFirst)
            services.TryAddSingleton<IExactOrnnSkillResolver, UnavailableResolver>();

        services.AddOrnnSkills();

        if (!fallbackFirst)
            services.TryAddSingleton<IExactOrnnSkillResolver, UnavailableResolver>();

        services.Last(descriptor => descriptor.ServiceType == typeof(IExactOrnnSkillResolver))
            .ImplementationType.Should().Be(typeof(OrnnExactAgentProfileSkillResolver));
    }

    private static OrnnExactAgentProfileSkillResolver CreateResolver(
        HttpMessageHandler handler,
        OrnnSkillPublishValidationPipeline? validationPipeline = null,
        TimeSpan? perCallTimeout = null,
        TimeProvider? timeProvider = null)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        var client = new OrnnSkillClient(
            new OrnnOptions { NyxIdSlug = "ornn" },
            nyxClient,
            perCallTimeout ?? OrnnSkillClient.DefaultPerCallTimeout,
            timeProvider: timeProvider);
        var mapper = new OrnnAgentProfileSkillPackageMapper(
            validationPipeline ?? new OrnnSkillPublishValidationPipeline());
        return new OrnnExactAgentProfileSkillResolver(client, mapper);
    }

    private static OrnnTestHttpMessageHandler SuccessHandler(
        IReadOnlyDictionary<string, string>? files = null) =>
        new(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailEnvelope()),
            _ => OrnnTestHttpMessageHandler.JsonResponse(JsonEnvelope(files: files)));

    private static ExactOrnnSkillReference ExactReference() => new()
    {
        SkillGuid = SkillGuid,
        LiteralVersion = LiteralVersion,
        ExpectedName = SkillName,
        ExpectedPublisherId = PublisherId,
    };

    private static string SkillMarkdown(string? scriptEntry = null) => $$"""
        ---
        name: {{SkillName}}
        description: Controls a home
        version: "{{LiteralVersion}}"
        arguments: device and action
        when-to-use: Use for exact home control
        disable-model-invocation: false
        user-invocable: true
        {{(scriptEntry is null ? string.Empty : $"script-entry: {scriptEntry}")}}
        metadata:
          category: tool-based
          tool-list:
            - tool-zeta
            - tool-alpha
        ---
        Inspect state.
        Apply requested change.
        """;

    private static string DetailEnvelope(
        string guid = SkillGuid,
        string name = SkillName,
        string publisher = PublisherId,
        string hash = "hash-alpha") =>
        JsonSerializer.Serialize(new
        {
            data = new
            {
                guid,
                name,
                description = "Controls a home",
                license = (string?)null,
                compatibility = (string?)null,
                metadata = new { category = "tool-based", tag = Array.Empty<string>() },
                tags = Array.Empty<string>(),
                skillHash = hash,
                isPrivate = true,
                createdBy = publisher,
                createdByEmail = "publisher@example.test",
                createdByDisplayName = "Publisher Alpha",
                createdOn = "2026-07-22T00:00:00.000Z",
                updatedOn = "2026-07-22T00:00:00.000Z",
                sharedWithUsers = Array.Empty<string>(),
                sharedWithOrgs = Array.Empty<string>(),
                version = LiteralVersion,
            },
            error = (object?)null,
        });

    private static string JsonEnvelope(
        string name = SkillName,
        string version = LiteralVersion,
        IReadOnlyDictionary<string, string>? files = null) =>
        JsonSerializer.Serialize(new
        {
            data = new
            {
                name,
                description = "Controls a home",
                version,
                metadata = new { category = "tool-based", tag = Array.Empty<string>() },
                files = files ?? new Dictionary<string, string>
                {
                    ["SKILL.md"] = SkillMarkdown(),
                },
            },
            error = (object?)null,
        });

    private sealed class MarkerAssetValidator(string assetKind, string marker)
        : IOrnnSkillPublishAssetValidator
    {
        public string AssetKind => assetKind;

        public Task<IReadOnlyList<OrnnSkillPublishDiagnostic>> ValidateAsync(
            OrnnSkillPublishRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var matchingPath = assetKind == OrnnSkillPublishValidationPipeline.WorkflowYamlAssetKind
                ? request.WorkflowYamls
                    .Where(asset => asset.Content.Contains(marker, StringComparison.Ordinal))
                    .Select(asset => $"workflows/{asset.WorkflowId}.yaml")
                    .FirstOrDefault()
                : request.Scripts
                    .Where(asset => asset.Content.Contains(marker, StringComparison.Ordinal))
                    .Select(asset => $"scripts/{Path.GetFileName(asset.Path)}")
                    .FirstOrDefault();

            IReadOnlyList<OrnnSkillPublishDiagnostic> diagnostics = matchingPath is null
                ? []
                : [new OrnnSkillPublishDiagnostic("raw_validator_code", $"raw compiler detail {marker}", matchingPath)];
            return Task.FromResult(diagnostics);
        }
    }

    private sealed class TrackingRemoteSkillFetcher : IRemoteSkillFetcher
    {
        public int Calls { get; private set; }

        public Task<SkillDefinition?> FetchSkillAsync(
            string accessToken,
            string nameOrId,
            CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("The exact Profile path called the name-capable fetcher.");
        }
    }

    private sealed class UnavailableResolver : IExactOrnnSkillResolver
    {
        public Task<ExactOrnnSkillResolutionResult> ResolveAsync(
            string nyxIdAccessToken,
            ExactOrnnSkillReference reference,
            CancellationToken ct = default) =>
            Task.FromResult(ExactOrnnSkillResolutionResult.Failed("ORNN_DEPENDENCY_UNAVAILABLE"));
    }

    private sealed class CancellationObservingHttpMessageHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public bool CancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                canceled);
            try
            {
                await canceled.Task;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }

            throw new InvalidOperationException("Cancellation handler completed without cancellation.");
        }
    }
}
