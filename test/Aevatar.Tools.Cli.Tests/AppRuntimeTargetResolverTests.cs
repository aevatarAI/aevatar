using Aevatar.Tools.Cli.Hosting;
using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Aevatar.Tools.Cli.Studio.Domain.Models;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class AppRuntimeTargetResolverTests
{
    [Fact]
    public async Task GetCurrentAsync_WhenWorkspaceUsesConfiguredLegacyDefault_ShouldMigrateToCurrentDefault()
    {
        var resolver = new AppRuntimeTargetResolver(
            new InMemoryStudioWorkspaceStore("http://localhost:6688/"),
            "http://localhost:6688",
            "https://runtime.example",
            embeddedCapabilitiesAvailable: false,
            legacyDefaultRuntimeBaseUrl: "http://localhost:6688/");

        var target = await resolver.GetCurrentAsync();

        target.ConfiguredBaseUrl.Should().Be("https://runtime.example");
        target.EffectiveBaseUrl.Should().Be("https://runtime.example");
        target.UsesLocalRuntime.Should().BeFalse();
    }

    [Fact]
    public async Task GetCurrentAsync_WhenCustomLegacyDefaultConfigured_ShouldUseConfiguredValue()
    {
        var resolver = new AppRuntimeTargetResolver(
            new InMemoryStudioWorkspaceStore("https://legacy.example/"),
            "http://localhost:6688",
            "https://runtime.example",
            embeddedCapabilitiesAvailable: false,
            legacyDefaultRuntimeBaseUrl: "https://legacy.example/");

        var target = await resolver.GetCurrentAsync();

        target.ConfiguredBaseUrl.Should().Be("https://runtime.example");
        target.EffectiveBaseUrl.Should().Be("https://runtime.example");
        target.UsesLocalRuntime.Should().BeFalse();
    }

    private sealed class InMemoryStudioWorkspaceStore : IStudioWorkspaceStore
    {
        private readonly StudioWorkspaceSettings _settings;

        public InMemoryStudioWorkspaceStore(string runtimeBaseUrl)
        {
            _settings = new StudioWorkspaceSettings(
                RuntimeBaseUrl: runtimeBaseUrl,
                Directories: [],
                AppearanceTheme: "blue",
                ColorMode: "light");
        }

        public Task<StudioWorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings);

        public Task SaveSettingsAsync(StudioWorkspaceSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredWorkflowFile>> ListWorkflowFilesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredWorkflowFile?> GetWorkflowFileAsync(string workflowId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredWorkflowFile> SaveWorkflowFileAsync(StoredWorkflowFile workflowFile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredExecutionRecord>> ListExecutionsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredExecutionRecord?> GetExecutionAsync(string executionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredExecutionRecord> SaveExecutionAsync(StoredExecutionRecord execution, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(StoredConnectorCatalog catalog, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorDraft> SaveConnectorDraftAsync(StoredConnectorDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteConnectorDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleCatalog> SaveRoleCatalogAsync(StoredRoleCatalog catalog, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredRoleDraft> SaveRoleDraftAsync(StoredRoleDraft draft, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteRoleDraftAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
