using System.Net;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.NyxId;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class OrnnExactRemoteSkillFetcherTests
{
    private const string SkillGuid = "11111111-1111-1111-1111-111111111111";
    private const string LiteralVersion = "1.2";
    private const string HashHex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    private static readonly ByteString HashBytes =
        ByteString.CopyFrom(Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());

    [Fact]
    public async Task FetchAsync_ShouldReadOnlyVersionPinnedGuidDetailAndJson()
    {
        var handler = SuccessHandler();
        var fetcher = CreateFetcher(handler);

        var result = await fetcher.FetchAsync("token", ExactRef());

        result.IsSuccess.Should().BeTrue();
        result.Should().BeEquivalentTo(ExactRemoteSkillFetchResult.Success(
            SkillGuid,
            LiteralVersion,
            "skill-alpha",
            "publisher-alpha",
            HashBytes,
            "# Skill Alpha\n\nInstructions."));
        handler.Requests.Select(request => request.RequestUri!.AbsoluteUri).Should().Equal(
            $"https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/{SkillGuid}?version=1.2",
            $"https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/{SkillGuid}/json?version=1.2");
        handler.Requests.Should().OnlyContain(request =>
            request.Method == HttpMethod.Get && request.Authorization!.Parameter == "token");
    }

    [Fact]
    public async Task FetchAsync_ShouldReadSkillMarkdownInsideSinglePackageDirectory()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson()),
            _ => OrnnTestHttpMessageHandler.JsonResponse(SkillJson(
                filesJson: "{\"skill-alpha/SKILL.md\":\"# Skill Alpha\\n\\nInstructions.\"}")));

        var result = await CreateFetcher(handler).FetchAsync("token", ExactRef());

        result.IsSuccess.Should().BeTrue();
        result.SkillMarkdown.Should().Be("# Skill Alpha\n\nInstructions.");
    }

    [Fact]
    public async Task FetchAsync_InvalidReferenceOrMissingToken_ShouldFailBeforeHttp()
    {
        var handler = SuccessHandler();
        var fetcher = CreateFetcher(handler);

        var missingToken = await fetcher.FetchAsync(" ", ExactRef());
        var invalidGuid = await fetcher.FetchAsync("token", new ExactRemoteSkillRef
        {
            Guid = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA",
            LiteralVersion = LiteralVersion,
        });
        var emptyGuid = await fetcher.FetchAsync("token", new ExactRemoteSkillRef
        {
            Guid = Guid.Empty.ToString("D"),
            LiteralVersion = LiteralVersion,
        });
        var invalidVersion = await fetcher.FetchAsync("token", new ExactRemoteSkillRef
        {
            Guid = SkillGuid,
            LiteralVersion = "latest",
        });

        missingToken.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.AccessTokenMissing);
        invalidGuid.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.InvalidReference);
        emptyGuid.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.InvalidReference);
        invalidVersion.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.InvalidReference);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_DetailGuidMismatch_ShouldFailWithoutFallback()
    {
        await AssertIdentityMismatchWithoutFallbackAsync(
            DetailJson(guid: "22222222-2222-2222-2222-222222222222"),
            SkillJson());
    }

    [Fact]
    public async Task FetchAsync_VersionMismatch_ShouldFailWithoutFallback()
    {
        await AssertIdentityMismatchWithoutFallbackAsync(
            DetailJson(),
            SkillJson(version: "1.3"));
    }

    [Fact]
    public async Task FetchAsync_DetailAndJsonNameMismatch_ShouldFailWithoutFallback()
    {
        await AssertIdentityMismatchWithoutFallbackAsync(
            DetailJson(name: "skill-alpha"),
            SkillJson(name: "skill-beta"));
    }

    [Fact]
    public async Task FetchAsync_MissingPublisherOrHashEvidence_ShouldFailClosed()
    {
        var cases = new[]
        {
            DetailJson(publisher: ""),
            DetailJson(hash: ""),
            DetailJson(hash: "not-a-sha256"),
        };

        foreach (var detailJson in cases)
        {
            var handler = new OrnnTestHttpMessageHandler(
                _ => OrnnTestHttpMessageHandler.JsonResponse(detailJson),
                _ => OrnnTestHttpMessageHandler.JsonResponse(SkillJson()));

            var result = await CreateFetcher(handler).FetchAsync("token", ExactRef());

            result.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.IntegrityEvidenceMissing);
            handler.Requests.Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task FetchAsync_MissingOrDuplicateSkillMarkdown_ShouldFailWithoutAlternateRead()
    {
        var skillJsonCases = new[]
        {
            SkillJson(filesJson: "{\"README.md\":\"readme\"}"),
            SkillJson(filesJson: "{\"SKILL.md\":\"one\",\"skill.md\":\"two\"}"),
        };

        foreach (var skillJson in skillJsonCases)
        {
            var handler = new OrnnTestHttpMessageHandler(
                _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson()),
                _ => OrnnTestHttpMessageHandler.JsonResponse(skillJson));

            var result = await CreateFetcher(handler).FetchAsync("token", ExactRef());

            result.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.InvalidResponse);
            result.FailureDetail.Should().Be("unique_skill_markdown_required");
            handler.Requests.Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task FetchAsync_NullExactResponses_ShouldFailClosed()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse("{}"),
            _ => OrnnTestHttpMessageHandler.JsonResponse("{}"));

        var result = await CreateFetcher(handler).FetchAsync("token", ExactRef());

        result.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.InvalidResponse);
        handler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.Forbidden, ExactRemoteSkillFetchFailureCode.AccessDenied)]
    [InlineData(false, HttpStatusCode.NotFound, ExactRemoteSkillFetchFailureCode.NotFound)]
    [InlineData(true, HttpStatusCode.InternalServerError, ExactRemoteSkillFetchFailureCode.InvalidResponse)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable, ExactRemoteSkillFetchFailureCode.InvalidResponse)]
    public async Task FetchAsync_ExactEndpointProxyFailure_ShouldPreserveTypedFailureWithoutFallback(
        bool failDetailEndpoint,
        HttpStatusCode statusCode,
        ExactRemoteSkillFetchFailureCode expectedFailureCode)
    {
        var handler = failDetailEndpoint
            ? new OrnnTestHttpMessageHandler(
                _ => OrnnTestHttpMessageHandler.JsonResponse("{\"error\":\"denied\"}", statusCode))
            : new OrnnTestHttpMessageHandler(
                _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson()),
                _ => OrnnTestHttpMessageHandler.JsonResponse("{\"error\":\"missing\"}", statusCode));

        var result = await CreateFetcher(handler).FetchAsync("token", ExactRef());

        result.FailureCode.Should().Be(expectedFailureCode);
        var expectedUris = failDetailEndpoint
            ? new[]
            {
                $"https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/{SkillGuid}?version=1.2",
            }
            : new[]
            {
                $"https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/{SkillGuid}?version=1.2",
                $"https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/{SkillGuid}/json?version=1.2",
            };
        handler.Requests.Select(request => request.RequestUri!.AbsoluteUri).Should().Equal(expectedUris);
    }

    [Fact]
    public async Task FetchAsync_InternalTimeout_ShouldReturnTypedTimeout()
    {
        var handler = new CancellationObservingHttpMessageHandler();
        var timeProvider = new FakeTimeProvider();

        var fetch = CreateFetcher(handler, TimeSpan.FromSeconds(1), timeProvider)
            .FetchAsync("token", ExactRef());
        await handler.Started;

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var result = await fetch;

        result.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.Timeout);
        handler.CancellationObserved.Should().BeTrue();
    }

    [Fact]
    public async Task FetchAsync_InvalidJsonException_ShouldReturnTypedFailure()
    {
        var handler = OrnnTestHttpMessageHandler.ReturningJson("not-json");

        var result = await CreateFetcher(handler).FetchAsync("token", ExactRef());

        result.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.Failed);
        result.FailureDetail.Should().Be("JsonException");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task FetchAsync_CallerCancellation_ShouldPropagate()
    {
        var handler = OrnnTestHttpMessageHandler.HangingUntilCanceled();
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();

        var act = async () => await CreateFetcher(handler)
            .FetchAsync("token", ExactRef(), callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static OrnnTestHttpMessageHandler SuccessHandler() =>
        new(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailJson()),
            _ => OrnnTestHttpMessageHandler.JsonResponse(SkillJson()));

    private static OrnnExactRemoteSkillFetcher CreateFetcher(
        HttpMessageHandler handler,
        TimeSpan? perCallTimeout = null,
        TimeProvider? timeProvider = null)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        var options = new OrnnOptions { NyxIdSlug = "ornn" };
        var client = perCallTimeout.HasValue
            ? new OrnnSkillClient(options, nyxClient, perCallTimeout.Value, timeProvider: timeProvider)
            : new OrnnSkillClient(options, nyxClient);
        return new OrnnExactRemoteSkillFetcher(client);
    }

    private static ExactRemoteSkillRef ExactRef() => new()
    {
        Guid = SkillGuid,
        LiteralVersion = LiteralVersion,
    };

    private static string DetailJson(
        string guid = SkillGuid,
        string name = "skill-alpha",
        string publisher = "publisher-alpha",
        string hash = HashHex) =>
        "{\"data\":{\"guid\":\"" + guid +
        "\",\"name\":\"" + name + "\",\"skillHash\":\"" + hash +
        "\",\"createdBy\":\"" + publisher + "\"}}";

    private static string SkillJson(
        string name = "skill-alpha",
        string version = LiteralVersion,
        string filesJson = "{\"SKILL.md\":\"# Skill Alpha\\n\\nInstructions.\"}") =>
        "{\"data\":{\"name\":\"" + name + "\",\"version\":\"" + version +
        "\",\"files\":" + filesJson + "}}";

    private static async Task AssertIdentityMismatchWithoutFallbackAsync(
        string detailJson,
        string skillJson)
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(detailJson),
            _ => OrnnTestHttpMessageHandler.JsonResponse(skillJson));

        var result = await CreateFetcher(handler).FetchAsync("token", ExactRef());

        result.FailureCode.Should().Be(ExactRemoteSkillFetchFailureCode.IdentityMismatch);
        handler.Requests.Select(request => request.RequestUri!.AbsoluteUri).Should().Equal(
            $"https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/{SkillGuid}?version=1.2",
            $"https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/{SkillGuid}/json?version=1.2");
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

            throw new InvalidOperationException("The cancellation-only handler completed without cancellation.");
        }
    }
}
