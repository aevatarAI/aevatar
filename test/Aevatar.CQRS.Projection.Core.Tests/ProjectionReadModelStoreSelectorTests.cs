using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionDocumentStoreSelectorTests
{
    [Fact]
    public void Select_WhenSingleProviderRegistered_ShouldReturnSingleProvider()
    {
        var registrations = new[]
        {
            CreateRegistration("inmemory", supportsIndexing: false),
        };

        var selected = ProjectionDocumentStoreSelector.Select(
            registrations,
            new ProjectionStoreSelectionOptions(),
            new ProjectionStoreRequirements());

        selected.ProviderName.Should().Be("inmemory");
    }

    [Fact]
    public void Select_WhenMultipleProvidersAndNoRequestedProvider_ShouldThrow()
    {
        var registrations = new[]
        {
            CreateRegistration("inmemory", supportsIndexing: false),
            CreateRegistration("elasticsearch", supportsIndexing: true, indexKinds: [ProjectionIndexKind.Document]),
        };

        Action act = () => ProjectionDocumentStoreSelector.Select(
            registrations,
            new ProjectionStoreSelectionOptions(),
            new ProjectionStoreRequirements());

        act.Should().Throw<ProjectionProviderSelectionException>()
            .Where(ex => ex.Reason.Contains("Multiple providers are registered", StringComparison.Ordinal));
    }

    [Fact]
    public void Select_WhenRequestedProviderMissing_ShouldThrow()
    {
        var registrations = new[]
        {
            CreateRegistration("inmemory", supportsIndexing: false),
        };

        Action act = () => ProjectionDocumentStoreSelector.Select(
            registrations,
            new ProjectionStoreSelectionOptions
            {
                RequestedProviderName = "elasticsearch",
            },
            new ProjectionStoreRequirements());

        act.Should().Throw<ProjectionProviderSelectionException>()
            .Where(ex => ex.Reason.Contains("Requested provider is not registered", StringComparison.Ordinal));
    }

    [Fact]
    public void Select_WhenCapabilitiesUnsupportedAndFailFastEnabled_ShouldThrow()
    {
        var registrations = new[]
        {
            CreateRegistration("inmemory", supportsIndexing: false),
        };

        Action act = () => ProjectionDocumentStoreSelector.Select(
            registrations,
            new ProjectionStoreSelectionOptions
            {
                RequestedProviderName = "inmemory",
                FailOnUnsupportedCapabilities = true,
            },
            new ProjectionStoreRequirements(
                requiresIndexing: true,
                requiredIndexKinds: [ProjectionIndexKind.Document]));

        act.Should().Throw<ProjectionProviderCapabilityValidationException>();
    }

    [Fact]
    public void Select_WhenRequiredIndexKindsAreNotFullySupported_ShouldThrow()
    {
        var registrations = new[]
        {
            CreateRegistration(
                "neo4j",
                supportsIndexing: true,
                indexKinds: [ProjectionIndexKind.Graph]),
        };

        Action act = () => ProjectionDocumentStoreSelector.Select(
            registrations,
            new ProjectionStoreSelectionOptions
            {
                RequestedProviderName = "neo4j",
                FailOnUnsupportedCapabilities = true,
            },
            new ProjectionStoreRequirements(
                requiresIndexing: true,
                requiredIndexKinds: [ProjectionIndexKind.Document, ProjectionIndexKind.Graph]));

        act.Should().Throw<ProjectionProviderCapabilityValidationException>()
            .WithMessage("*not fully supported*");
    }

    [Fact]
    public void Select_WhenCapabilitiesUnsupportedAndFailFastDisabled_ShouldReturnProvider()
    {
        var registrations = new[]
        {
            CreateRegistration("inmemory", supportsIndexing: false),
        };

        var selected = ProjectionDocumentStoreSelector.Select(
            registrations,
            new ProjectionStoreSelectionOptions
            {
                RequestedProviderName = "inmemory",
                FailOnUnsupportedCapabilities = false,
            },
            new ProjectionStoreRequirements(
                requiresIndexing: true,
                requiredIndexKinds: [ProjectionIndexKind.Document]));

        selected.ProviderName.Should().Be("inmemory");
    }

    private static IProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>> CreateRegistration(
        string providerName,
        bool supportsIndexing,
        IEnumerable<ProjectionIndexKind>? indexKinds = null)
    {
        var capabilities = new ProjectionProviderCapabilities(
            providerName,
            supportsIndexing,
            indexKinds);

        return new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TestReadModel, string>>(
            providerName,
            capabilities,
            _ => new NoopStore());
    }

    private sealed class NoopStore : IDocumentProjectionStore<TestReadModel, string>
    {
        public Task UpsertAsync(TestReadModel readModel, CancellationToken ct = default) => Task.CompletedTask;

        public Task MutateAsync(string key, Action<TestReadModel> mutate, CancellationToken ct = default) => Task.CompletedTask;

        public Task<TestReadModel?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<TestReadModel?>(null);

        public Task<IReadOnlyList<TestReadModel>> ListAsync(int take = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TestReadModel>>([]);
    }

    private sealed class TestReadModel;
}
