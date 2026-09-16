using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.Mainnet.Host.Api.Skills;
using FluentAssertions;
using Google.Protobuf;
using NSubstitute;

namespace Aevatar.Capabilities.Tests;

public sealed class UserSkillCatalogQueryServiceTests
{
    private const string SkillGuid = "11111111-2222-4333-8444-555555555555";

    [Fact]
    public async Task GetExactSkillAsync_ShouldMapAuthoritativePackageAndDeclaredTools()
    {
        var resolver = Substitute.For<IExactOrnnSkillResolver>();
        resolver.ResolveAsync(
                "token-alpha",
                Arg.Is<ExactRemoteSkillRef>(reference =>
                    reference.Guid == SkillGuid && reference.LiteralVersion == "1.4"),
                Arg.Any<CancellationToken>())
            .Returns(ExactOrnnSkillResolutionResult.Success(new ResolvedOrnnSkillPackage
            {
                SkillGuid = SkillGuid,
                LiteralVersion = "1.4",
                CanonicalName = "aevatar-operations",
                PublisherId = "aevatar-platform",
                SkillSha256 = ByteString.CopyFrom(
                    Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray()),
                SkillMarkdownUtf8Bytes = 128,
                DeclaredToolNames = ["aevatar_write", "", " aevatar_read ", "aevatar_write"],
            }));
        var service = NewService(resolver);

        var result = await service.GetExactSkillAsync(
            "token-alpha",
            SkillGuid,
            "1.4",
            CancellationToken.None);

        result.Error.Should().BeNull();
        result.Detail.Should().NotBeNull();
        result.Detail!.Guid.Should().Be(SkillGuid);
        result.Detail.LiteralVersion.Should().Be("1.4");
        result.Detail.Name.Should().Be("aevatar-operations");
        result.Detail.Publisher.Should().Be("aevatar-platform");
        result.Detail.SkillHash.Should().Be(
            "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        result.Detail.DeclaredToolNames.Should().Equal("aevatar_read", "aevatar_write");
    }

    [Fact]
    public async Task GetExactSkillAsync_ShouldPreserveAccessDeniedBoundary()
    {
        var resolver = Substitute.For<IExactOrnnSkillResolver>();
        resolver.ResolveAsync(
                Arg.Any<string>(),
                Arg.Any<ExactRemoteSkillRef>(),
                Arg.Any<CancellationToken>())
            .Returns(ExactOrnnSkillResolutionResult.Failure("ORNN_SKILL_ACCESS_DENIED"));
        var service = NewService(resolver);

        var result = await service.GetExactSkillAsync(
            "token-alpha",
            SkillGuid,
            "1.4",
            CancellationToken.None);

        result.Detail.Should().BeNull();
        result.UpstreamStatus.Should().Be(403);
    }

    private static UserSkillCatalogQueryService NewService(IExactOrnnSkillResolver resolver)
    {
        var nyxId = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new HttpClient(new RejectingHandler()));
        var ornn = new OrnnSkillClient(
            new OrnnOptions { NyxIdSlug = "ornn-api" },
            nyxId);
        return new UserSkillCatalogQueryService(
            ornn,
            Substitute.For<IRemoteSkillFetcher>(),
            resolver);
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "A literal exact read must use the authoritative resolver only.");
    }
}
