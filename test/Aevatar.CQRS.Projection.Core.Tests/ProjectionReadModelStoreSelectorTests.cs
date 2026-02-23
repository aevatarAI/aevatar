using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionReadModelStoreSelectorTests
{
    [Fact]
    public void Select_WhenSingleProviderRegistered_ShouldReturnSingleProvider()
    {
        var registrations = new[]
        {
            CreateRegistration("inmemory", supportsIndexing: false),
        };

        var selected = ProjectionReadModelStoreSelector.Select(
            registrations,
            new ProjectionReadModelStoreSelectionOptions(),
            new ProjectionReadModelRequirements());

        selected.ProviderName.Should().Be("inmemory");
    }

    [Fact]
    public void Select_WhenMultipleProvidersAndNoRequestedProvider_ShouldThrow()
    {
        var registrations = new[]
        {
            CreateRegistration("inmemory", supportsIndexing: false),
            CreateRegistration("elasticsearch", supportsIndexing: true, indexKinds: [ProjectionReadModelIndexKind.Document]),
        };

        Action act = () => ProjectionReadModelStoreSelector.Select(
            registrations,
            new ProjectionReadModelStoreSelectionOptions(),
            new ProjectionReadModelRequirements());

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

        Action act = () => ProjectionReadModelStoreSelector.Select(
            registrations,
            new ProjectionReadModelStoreSelectionOptions
            {
                RequestedProviderName = "elasticsearch",
            },
            new ProjectionReadModelRequirements());

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

        Action act = () => ProjectionReadModelStoreSelector.Select(
            registrations,
            new ProjectionReadModelStoreSelectionOptions
            {
                RequestedProviderName = "inmemory",
                FailOnUnsupportedCapabilities = true,
            },
            new ProjectionReadModelRequirements(
                requiresIndexing: true,
                requiredIndexKinds: [ProjectionReadModelIndexKind.Document]));

        act.Should().Throw<ProjectionReadModelCapabilityValidationException>();
    }

    [Fact]
    public void Select_WhenCapabilitiesUnsupportedAndFailFastDisabled_ShouldReturnProvider()
    {
        var registrations = new[]
        {
            CreateRegistration("inmemory", supportsIndexing: false),
        };

        var selected = ProjectionReadModelStoreSelector.Select(
            registrations,
            new ProjectionReadModelStoreSelectionOptions
            {
                RequestedProviderName = "inmemory",
                FailOnUnsupportedCapabilities = false,
            },
            new ProjectionReadModelRequirements(
                requiresIndexing: true,
                requiredIndexKinds: [ProjectionReadModelIndexKind.Document]));

        selected.ProviderName.Should().Be("inmemory");
    }

    private static IProjectionReadModelStoreRegistration<TestReadModel, string> CreateRegistration(
        string providerName,
        bool supportsIndexing,
        IEnumerable<ProjectionReadModelIndexKind>? indexKinds = null)
    {
        var capabilities = new ProjectionReadModelProviderCapabilities(
            providerName,
            supportsIndexing,
            indexKinds);

        return new DelegateProjectionReadModelStoreRegistration<TestReadModel, string>(
            providerName,
            capabilities,
            _ => new NoopStore());
    }

    private sealed class NoopStore : IProjectionReadModelStore<TestReadModel, string>
    {
        public Task UpsertAsync(TestReadModel readModel, CancellationToken ct = default) => Task.CompletedTask;

        public Task MutateAsync(string key, Action<TestReadModel> mutate, CancellationToken ct = default) => Task.CompletedTask;

        public Task<TestReadModel?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<TestReadModel?>(null);

        public Task<IReadOnlyList<TestReadModel>> ListAsync(int take = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TestReadModel>>([]);
    }

    private sealed class TestReadModel;
}
