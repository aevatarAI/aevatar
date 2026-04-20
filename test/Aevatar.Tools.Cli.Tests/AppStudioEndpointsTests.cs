using Aevatar.Studio.Application.Scripts.Contracts;
using Aevatar.Studio.Hosting.Endpoints;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions.Connectors;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Tools.Cli.Tests;

public class AppStudioEndpointsTests
{
    [Fact]
    public void NormalizeStudioDocumentId_ShouldSlugifyReadableNames()
    {
        var result = StudioEndpoints.NormalizeStudioDocumentId(
            " Customer Support Workflow 2026 ",
            "workflow");

        result.Should().Be("customer-support-workflow-2026");
    }

    [Fact]
    public void NormalizeStudioDocumentId_WhenInputIsBlank_ShouldUseFallbackPrefix()
    {
        var result = StudioEndpoints.NormalizeStudioDocumentId(
            "   ",
            "script");

        result.Should().StartWith("script-");
    }

    [Fact]
    public void AppScriptProtocol_ShouldRoundTripStringsAndLists()
    {
        var state = AppScriptProtocol.CreateState(
            input: "hello",
            output: "HELLO",
            status: "ok",
            lastCommandId: "command-1",
            notes: ["trimmed", "uppercased"]);

        AppScriptProtocol.GetString(state, AppScriptProtocol.InputField).Should().Be("hello");
        AppScriptProtocol.GetString(state, AppScriptProtocol.OutputField).Should().Be("HELLO");
        AppScriptProtocol.GetString(state, AppScriptProtocol.StatusField).Should().Be("ok");
        AppScriptProtocol.GetString(state, AppScriptProtocol.LastCommandIdField).Should().Be("command-1");
        AppScriptProtocol.GetStringList(state, AppScriptProtocol.NotesField).Should().Equal("trimmed", "uppercased");
    }

    [Fact]
    public async Task InjectAuthoringLLMMetadataAsync_ShouldNotInheritUserRoutePreferenceByDefault()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INyxIdUserLlmPreferencesStore>(
            new StubNyxIdUserLlmPreferencesStore(
                new NyxIdUserLlmPreferences(
                    DefaultModel: "claude-sonnet-4-5-20250929",
                    PreferredRoute: "/api/v1/proxy/s/chrono-llm-2")));
        await using var provider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        httpContext.Request.Headers.Authorization = "Bearer token-123";

        var metadata = await StudioEndpoints.InjectAuthoringLLMMetadataAsync(
            httpContext,
            new Dictionary<string, string>
            {
                ["source"] = "studio-test",
            },
            CancellationToken.None);

        metadata[LLMRequestMetadataKeys.NyxIdAccessToken].Should().Be("token-123");
        metadata[ConnectorRequest.HttpAuthorizationMetadataKey].Should().Be("Bearer token-123");
        metadata[LLMRequestMetadataKeys.ModelOverride].Should().Be("claude-sonnet-4-5-20250929");
        metadata["source"].Should().Be("studio-test");
        metadata.Should().NotContainKey(LLMRequestMetadataKeys.NyxIdRoutePreference);
    }

    [Fact]
    public async Task InjectAuthoringLLMMetadataAsync_ShouldPreserveExplicitClientRoutePreference()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INyxIdUserLlmPreferencesStore>(
            new StubNyxIdUserLlmPreferencesStore(
                new NyxIdUserLlmPreferences(
                    DefaultModel: "claude-sonnet-4-5-20250929",
                    PreferredRoute: "/api/v1/proxy/s/chrono-llm-2")));
        await using var provider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        httpContext.Request.Headers.Authorization = "Bearer token-123";

        var metadata = await StudioEndpoints.InjectAuthoringLLMMetadataAsync(
            httpContext,
            new Dictionary<string, string>
            {
                ["source"] = "studio-test",
                [LLMRequestMetadataKeys.NyxIdRoutePreference] = string.Empty,
            },
            CancellationToken.None);

        metadata[LLMRequestMetadataKeys.NyxIdAccessToken].Should().Be("token-123");
        metadata[ConnectorRequest.HttpAuthorizationMetadataKey].Should().Be("Bearer token-123");
        metadata[LLMRequestMetadataKeys.ModelOverride].Should().Be("claude-sonnet-4-5-20250929");
        metadata["source"].Should().Be("studio-test");
        metadata[LLMRequestMetadataKeys.NyxIdRoutePreference].Should().BeEmpty();
    }

    private sealed class StubNyxIdUserLlmPreferencesStore : INyxIdUserLlmPreferencesStore
    {
        private readonly NyxIdUserLlmPreferences _preferences;

        public StubNyxIdUserLlmPreferencesStore(NyxIdUserLlmPreferences preferences)
        {
            _preferences = preferences;
        }

        public Task<NyxIdUserLlmPreferences> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_preferences);
    }
}
