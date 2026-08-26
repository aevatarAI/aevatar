using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class ConnectorServiceTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("npx", "https://mcp.example.com/mcp")]
    public async Task SaveCatalogAsync_WhenMcpTransportTargetIsNotExclusive_ShouldRejectBeforePersistence(
        string command,
        string url)
    {
        var commandPort = new RecordingConnectorCatalogCommandPort();
        var service = new ConnectorService(null!, commandPort, null!);
        var request = new SaveConnectorCatalogRequest([CreateMcpConnector(command, url)]);

        var action = () => service.SaveCatalogAsync(request);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires exactly one of mcp.command or mcp.url*");
        commandPort.SaveAttempts.Should().Be(0);
    }

    [Fact]
    public async Task SaveCatalogAsync_ShouldNormalizeHostCallbackConfiguration()
    {
        var commandPort = new RecordingConnectorCatalogCommandPort();
        var service = new ConnectorService(null!, commandPort, null!);

        var response = await service.SaveCatalogAsync(new SaveConnectorCatalogRequest(
            [CreateHostCallbackConnector()]));

        response.Connectors.Should().ContainSingle();
        var stored = commandPort.LastCatalog!.Connectors.Should().ContainSingle().Subject;
        stored.Type.Should().Be("host_callback");
        stored.HostCallback.Handler.Should().Be("deterministic_compute");
        stored.HostCallback.AllowedOperations.Should().Equal("sha256_utf8");
        stored.HostCallback.AllowedInputKeys.Should().Equal("text");
    }

    [Fact]
    public async Task SaveCatalogAsync_ShouldAcceptNonHostConnector_WhenHostCallbackConfigurationIsOmitted()
    {
        var commandPort = new RecordingConnectorCatalogCommandPort();
        var service = new ConnectorService(null!, commandPort, null!);
        var connector = CreateMcpConnector("npx", string.Empty) with { HostCallback = null };

        var response = await service.SaveCatalogAsync(new SaveConnectorCatalogRequest([connector]));

        response.Connectors.Should().ContainSingle();
        commandPort.LastCatalog!.Connectors.Should().ContainSingle()
            .Which.HostCallback.Should().BeEquivalentTo(
                new StoredHostCallbackConnectorConfig(string.Empty, [], []));
    }

    private static ConnectorDefinitionDto CreateMcpConnector(string command, string url) =>
        new(
            "mcp-canary",
            "mcp",
            true,
            30_000,
            0,
            new HttpConnectorDefinitionDto(
                string.Empty,
                [],
                [],
                [],
                new Dictionary<string, string>(),
                EmptyAuth()),
            new CliConnectorDefinitionDto(
                string.Empty,
                [],
                [],
                [],
                string.Empty,
                new Dictionary<string, string>()),
            new McpConnectorDefinitionDto(
                "mcp-canary",
                command,
                url,
                [],
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                EmptyAuth(),
                string.Empty,
                [],
                []),
            EmptyHostCallback());

    private static ConnectorDefinitionDto CreateHostCallbackConnector() =>
        new(
            "deterministic-hash",
            "host_callback",
            true,
            30_000,
            0,
            new HttpConnectorDefinitionDto(
                string.Empty,
                [],
                [],
                [],
                new Dictionary<string, string>(),
                EmptyAuth()),
            new CliConnectorDefinitionDto(
                string.Empty,
                [],
                [],
                [],
                string.Empty,
                new Dictionary<string, string>()),
            new McpConnectorDefinitionDto(
                string.Empty,
                string.Empty,
                string.Empty,
                [],
                new Dictionary<string, string>(),
                new Dictionary<string, string>(),
                EmptyAuth(),
                string.Empty,
                [],
                []),
            new HostCallbackConnectorDefinitionDto(
                " deterministic_compute ",
                [" sha256_utf8 ", "sha256_utf8"],
                [" text "]));

    private static HostCallbackConnectorDefinitionDto EmptyHostCallback() =>
        new(string.Empty, [], []);

    private static ConnectorAuthDefinitionDto EmptyAuth() =>
        new(
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);

    private sealed class RecordingConnectorCatalogCommandPort : IConnectorCatalogCommandPort
    {
        public int SaveAttempts { get; private set; }
        public StoredConnectorCatalog? LastCatalog { get; private set; }

        public Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(
            StoredConnectorCatalog catalog,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            LastCatalog = catalog;
            return Task.FromResult(catalog);
        }

        public Task<ImportedConnectorCatalog> ImportLocalCatalogAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StoredConnectorDraft> SaveConnectorDraftAsync(
            StoredConnectorDraft draft,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteConnectorDraftAsync(
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
