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
                []));

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

        public Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(
            StoredConnectorCatalog catalog,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
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
