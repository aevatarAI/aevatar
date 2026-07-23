using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.QueryPorts;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Studio.Tests;

public sealed class ProjectionUserConfigQueryPortTests
{
    [Fact]
    public void UserConfig_ShouldDefaultCompatibilityRouteToEmpty()
    {
        var config = new UserConfig(DefaultModel: string.Empty);

        config.PreferredLlmRoute.Should().BeEmpty();
    }

    [Theory]
    [InlineData(UserConfigResourceKind.OwnerScope, "scope-alpha", "user-config-scope-alpha")]
    [InlineData(UserConfigResourceKind.ChannelBinding, "binding-alpha", "channel-user-config-binding-alpha")]
    public async Task GetAsync_ShouldReadActorForResourceKind(
        UserConfigResourceKind kind,
        string value,
        string expectedActorId)
    {
        var reader = new RecordingDocumentReader();
        var port = CreatePort(reader);

        await port.GetAsync(new UserConfigResourceKey(kind, value));

        reader.GetKeys.Should().ContainSingle().Which.Should().Be(expectedActorId);
    }

    [Fact]
    public async Task GetAsync_WithoutTypedSelection_ShouldIgnoreConflictingCompatibilityRoute()
    {
        var reader = new RecordingDocumentReader
        {
            Document = new UserConfigCurrentStateDocument
            {
                PreferredLlmRoute = "/api/v1/proxy/s/legacy",
            },
        };
        var port = CreatePort(reader);

        var config = await port.GetAsync(UserConfigResourceKey.ForOwnerScope("scope-alpha"));

        config.LlmSelection.Should().BeNull();
        config.PreferredLlmRoute.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_WithTypedUnspecified_ShouldIgnoreConflictingCompatibilityRoute()
    {
        var reader = new RecordingDocumentReader
        {
            Document = new UserConfigCurrentStateDocument
            {
                PreferredLlmRoute = "/api/v1/proxy/s/legacy",
                LlmSelection = new UserLlmSelection
                {
                    RouteKind = UserLlmRouteKind.Unspecified,
                    RouteValue = "/api/v1/proxy/s/typed-but-ignored",
                    NyxIdUserServiceId = "us-ignored",
                    ServiceSlugSnapshot = "ignored",
                },
            },
        };
        var port = CreatePort(reader);

        var config = await port.GetAsync(UserConfigResourceKey.ForOwnerScope("scope-alpha"));

        config.LlmSelection.Should().NotBeNull();
        config.LlmSelection!.Kind.Should().Be(UserLlmSelectionKind.Unspecified);
        config.PreferredLlmRoute.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_WithTypedGateway_ShouldUseCanonicalGatewayDespiteConflictingCompatibilityRoute()
    {
        var reader = new RecordingDocumentReader
        {
            Document = new UserConfigCurrentStateDocument
            {
                PreferredLlmRoute = "/api/v1/proxy/s/legacy",
                LlmSelection = new UserLlmSelection
                {
                    RouteKind = UserLlmRouteKind.Gateway,
                    RouteValue = "/api/v1/proxy/s/typed-but-ignored",
                    NyxIdUserServiceId = "us-ignored",
                    ServiceSlugSnapshot = "ignored",
                },
            },
        };
        var port = CreatePort(reader);

        var config = await port.GetAsync(UserConfigResourceKey.ForOwnerScope("scope-alpha"));

        config.LlmSelection.Should().NotBeNull();
        config.LlmSelection!.Kind.Should().Be(UserLlmSelectionKind.Gateway);
        config.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
    }

    [Fact]
    public async Task GetAsync_WithTypedService_ShouldUseTrimmedTypedRouteDespiteConflictingCompatibilityRoute()
    {
        var reader = new RecordingDocumentReader
        {
            Document = new UserConfigCurrentStateDocument
            {
                DefaultModel = "gpt-5.5",
                PreferredLlmRoute = "/api/v1/proxy/s/legacy",
                LlmSelection = new UserLlmSelection
                {
                    RouteKind = UserLlmRouteKind.NyxIdUserService,
                    RouteValue = " route-alpha ",
                    NyxIdUserServiceId = "us-alpha",
                    ServiceSlugSnapshot = "service-alpha",
                },
            },
        };
        var port = CreatePort(reader);

        var config = await port.GetAsync(UserConfigResourceKey.ForOwnerScope("scope-alpha"));

        config.LlmSelection.Should().Be(new UserLlmSelectionValue(
            UserLlmSelectionKind.NyxIdUserService,
            " route-alpha ",
            "us-alpha",
            "service-alpha"));
        config.PreferredLlmRoute.Should().Be("route-alpha");
    }

    [Fact]
    public async Task GetAsync_ShouldLeaveTypedSelectionNull_WhenDocumentIsMissing()
    {
        var port = CreatePort(new RecordingDocumentReader());

        var config = await port.GetAsync(UserConfigResourceKey.ForOwnerScope("scope-alpha"));

        config.LlmSelection.Should().BeNull();
        config.PreferredLlmRoute.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_ShouldKeepCompatibilityRouteEmpty_WhenProjectedSelectionIsMissing()
    {
        var port = CreatePort(new RecordingDocumentReader
        {
            Document = new UserConfigCurrentStateDocument(),
        });

        var config = await port.GetAsync(UserConfigResourceKey.ForOwnerScope("scope-alpha"));

        config.LlmSelection.Should().BeNull();
        config.PreferredLlmRoute.Should().BeEmpty();
    }

    private static ProjectionUserConfigQueryPort CreatePort(RecordingDocumentReader reader) =>
        new(reader, new StubScopeResolver(), new StubUserConfigDefaults());

    private sealed class RecordingDocumentReader
        : IProjectionDocumentReader<UserConfigCurrentStateDocument, string>
    {
        public UserConfigCurrentStateDocument? Document { get; init; }
        public List<string> GetKeys { get; } = [];

        public Task<UserConfigCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            GetKeys.Add(key);
            return Task.FromResult(Document);
        }

        public Task<ProjectionDocumentQueryResult<UserConfigCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionDocumentQueryResult<UserConfigCurrentStateDocument>.Empty);
    }

    private sealed class StubScopeResolver : IAppScopeResolver
    {
        public AppScopeContext? Resolve(HttpContext? httpContext = null) =>
            new("ambient-scope", "test");

        public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null) => false;
    }

    private sealed class StubUserConfigDefaults : IUserConfigDefaults
    {
        public string LocalRuntimeBaseUrl => "http://127.0.0.1:5080";
        public string RemoteRuntimeBaseUrl => "https://runtime.example.com";
    }
}
