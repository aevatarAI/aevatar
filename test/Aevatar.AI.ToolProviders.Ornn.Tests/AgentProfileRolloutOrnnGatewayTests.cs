using System.Net;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.Tools.AgentProfileRollout;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.ToolProviders.Ornn.Tests;

public sealed class AgentProfileRolloutOrnnGatewayTests
{
    private const string SkillGuid = "2d05bf2e-88ee-4f76-9998-728ba2f9db10";
    private const string LiteralVersion = "1.4";
    private const string SkillName = "skill-alpha";
    private const string PublisherId = "publisher-alpha";

    [Fact]
    public async Task ReadExactSkillAsync_should_use_literal_version_endpoints_and_map_response()
    {
        var handler = SuccessHandler();

        var result = await CreateGateway(handler).ReadExactSkillAsync(
            "access-token",
            SkillGuid,
            LiteralVersion,
            CancellationToken.None);

        result.Should().Be(new VerifiedExactOrnnSkill(
            SkillGuid,
            SkillName,
            LiteralVersion,
            PublisherId));
        handler.Requests.Select(static request => request.RequestUri!.AbsoluteUri).Should().Equal(
            $"https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/{SkillGuid}?version={LiteralVersion}",
            $"https://nyx.example/api/v1/proxy/s/ornn/api/v1/skills/{SkillGuid}/json?version={LiteralVersion}");
        handler.Requests.Should().OnlyContain(request =>
            request.Method == HttpMethod.Get &&
            request.Authorization != null &&
            request.Authorization.Scheme == "Bearer" &&
            request.Authorization.Parameter == "access-token");
    }

    [Fact]
    public async Task ReadExactSkillAsync_should_reject_detail_package_name_mismatch()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailEnvelope()),
            _ => OrnnTestHttpMessageHandler.JsonResponse(PackageEnvelope(name: "skill-beta")));

        var act = async () => await CreateGateway(handler).ReadExactSkillAsync(
            "token",
            SkillGuid,
            LiteralVersion,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*identity mismatch*");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ProvisionAsync_should_reject_handler_backed_publisher_mismatch()
    {
        using var releaseInput = new TemporaryDirectory();
        using var outputParent = new TemporaryDirectory();
        var outputDirectory = Path.Combine(outputParent.Path, "output");
        var releaseSpec = BuildReleaseSpec();
        var releaseSpecPath = Path.Combine(releaseInput.Path, AgentProfileRolloutCommands.ReleaseSpecFileName);
        await File.WriteAllBytesAsync(
            releaseSpecPath,
            AgentProfileRolloutCommands.FormatReleaseSpecUtf8(releaseSpec));
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailEnvelope(publisher: "publisher-beta")),
            _ => OrnnTestHttpMessageHandler.JsonResponse(PackageEnvelope()));
        var commands = new AgentProfileRolloutCommands(CreateGateway(handler));

        var exitCode = await commands.ProvisionAsync(
            "token",
            releaseSpecPath,
            outputDirectory,
            CancellationToken.None);

        exitCode.Should().Be(1);
        handler.Requests.Should().HaveCount(2);
        Directory.Exists(outputDirectory).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task ReadExactSkillAsync_should_fail_closed_on_proxy_failure(
        bool failDetail,
        int expectedRequestCount)
    {
        var handler = failDetail
            ? new OrnnTestHttpMessageHandler(
                _ => OrnnTestHttpMessageHandler.JsonResponse(
                    "{\"error\":\"forbidden\"}",
                    HttpStatusCode.Forbidden))
            : new OrnnTestHttpMessageHandler(
                _ => OrnnTestHttpMessageHandler.JsonResponse(DetailEnvelope()),
                _ => OrnnTestHttpMessageHandler.JsonResponse(
                    "{\"error\":\"unavailable\"}",
                    HttpStatusCode.BadGateway));

        var act = async () => await CreateGateway(handler).ReadExactSkillAsync(
            "token",
            SkillGuid,
            LiteralVersion,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*read-back failed*");
        handler.Requests.Should().HaveCount(expectedRequestCount);
    }

    [Fact]
    public async Task ReadExactSkillAsync_should_stop_when_detail_is_unavailable()
    {
        var handler = new OrnnTestHttpMessageHandler(
            _ => OrnnTestHttpMessageHandler.JsonResponse("{\"data\":null}"),
            _ => OrnnTestHttpMessageHandler.JsonResponse(PackageEnvelope()));

        var act = async () => await CreateGateway(handler).ReadExactSkillAsync(
            "token",
            SkillGuid,
            LiteralVersion,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*detail is unavailable*");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ReadExactSkillAsync_should_propagate_caller_cancellation()
    {
        var handler = OrnnTestHttpMessageHandler.HangingUntilCanceled();
        using var cts = new CancellationTokenSource();

        var pending = CreateGateway(handler).ReadExactSkillAsync(
            "token",
            SkillGuid,
            LiteralVersion,
            cts.Token);
        cts.Cancel();

        var act = async () => await pending;
        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().ContainSingle();
    }

    private static OrnnAgentProfileRolloutGateway CreateGateway(HttpMessageHandler handler)
    {
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(handler));
        var ornnClient = new OrnnSkillClient(
            new OrnnOptions { NyxIdSlug = "ornn" },
            nyxClient);
        return new OrnnAgentProfileRolloutGateway(ornnClient);
    }

    private static OrnnTestHttpMessageHandler SuccessHandler() =>
        new(
            _ => OrnnTestHttpMessageHandler.JsonResponse(DetailEnvelope()),
            _ => OrnnTestHttpMessageHandler.JsonResponse(PackageEnvelope()));

    private static string DetailEnvelope(
        string name = SkillName,
        string publisher = PublisherId) =>
        JsonSerializer.Serialize(new
        {
            data = new
            {
                guid = SkillGuid,
                name,
                createdBy = publisher,
            },
        });

    private static string PackageEnvelope(
        string name = SkillName,
        string version = LiteralVersion) =>
        JsonSerializer.Serialize(new
        {
            data = new
            {
                name,
                version,
            },
        });

    private static AgentProfileRolloutReleaseSpec BuildReleaseSpec()
    {
        var releaseSpec = new AgentProfileRolloutReleaseSpec
        {
            ReleaseId = "gateway-publisher-mismatch",
            Stage = "shadow-canary",
            ProfileReference = new AgentProfileReference
            {
                OwnerHandle = "system",
                ProfileSlug = "nyxid-chat",
            },
            ActivationMode = AgentProfileRolloutActivationMode.Shadow,
            CohortSalt = "gateway-publisher-mismatch",
            CohortBasisPoints = 500,
            ExpectedPublishedRevision = 17,
            ExpectedPublishedSnapshotSha256 = ByteString.CopyFrom(new byte[32]),
            RuntimeBounds = new AgentProfileRolloutRuntimeBounds
            {
                MaxPlanSteps = 4,
                HandoffTtlSeconds = 900,
                ClassifierTimeoutMs = 600,
                MaxSelectedSkillBytes = 24_576,
            },
        };
        releaseSpec.ExpectedExactSkillClosure.Add(new ExactOrnnSkillReference
        {
            SkillGuid = SkillGuid,
            LiteralVersion = LiteralVersion,
            ExpectedName = SkillName,
            ExpectedPublisherId = PublisherId,
        });
        return releaseSpec;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aevatar-rollout-gateway-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
