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
    public async Task GetAsync_ShouldMapCompleteTypedSelection()
    {
        var reader = new RecordingDocumentReader
        {
            Document = new UserConfigCurrentStateDocument
            {
                DefaultModel = "gpt-5.5",
                PreferredLlmRoute = "/api/v1/proxy/s/chrono-llm-public",
                LlmSelection = new UserLlmSelection
                {
                    RouteKind = UserLlmRouteKind.NyxIdUserService,
                    RouteValue = "/api/v1/proxy/s/chrono-llm-public",
                    NyxIdUserServiceId = "us-alpha",
                    ServiceSlugSnapshot = "chrono-llm-public",
                },
            },
        };
        var port = CreatePort(reader);

        var config = await port.GetAsync(UserConfigResourceKey.ForOwnerScope("scope-alpha"));

        config.LlmSelection.Should().Be(new UserLlmSelectionValue(
            UserLlmSelectionKind.NyxIdUserService,
            "/api/v1/proxy/s/chrono-llm-public",
            "us-alpha",
            "chrono-llm-public"));
    }

    [Fact]
    public async Task GetAsync_ShouldLeaveTypedSelectionNull_WhenDocumentIsMissing()
    {
        var port = CreatePort(new RecordingDocumentReader());

        var config = await port.GetAsync(UserConfigResourceKey.ForOwnerScope("scope-alpha"));

        config.LlmSelection.Should().BeNull();
        config.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
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
