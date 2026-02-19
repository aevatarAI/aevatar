using Aevatar.CQRS.Runtime.Abstractions.Configuration;
using Aevatar.Platform.Application.Abstractions.Commands;
using Aevatar.Platform.Infrastructure.Store;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.Integration.Tests;

public sealed class FileSystemPlatformCommandStateStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemPlatformCommandStateStore _store;

    public FileSystemPlatformCommandStateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fs_cmd_store_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var options = Options.Create(new CqrsRuntimeOptions { WorkingDirectory = _tempDir });
        _store = new FileSystemPlatformCommandStateStore(options);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("../secret")]
    [InlineData("..\\windows\\system32\\config")]
    [InlineData("sub/../../../escape")]
    public async Task GetAsync_WithPathTraversal_ShouldReturnNull(string maliciousId)
    {
        var result = await _store.GetAsync(maliciousId);
        result.Should().BeNull();
    }

    [Fact]
    public async Task UpsertAsync_ThenGetAsync_ShouldRoundTrip()
    {
        var id = Guid.NewGuid().ToString("N");
        var status = new PlatformCommandStatus
        {
            CommandId = id,
            Subsystem = "test",
            Command = "echo",
            State = "Completed",
            Succeeded = true,
        };

        await _store.UpsertAsync(status);
        var loaded = await _store.GetAsync(id);

        loaded.Should().NotBeNull();
        loaded!.CommandId.Should().Be(id);
        loaded.Subsystem.Should().Be("test");
        loaded.State.Should().Be("Completed");
    }

    [Fact]
    public async Task GetAsync_WithValidGuid_WhenNotStored_ShouldReturnNull()
    {
        var result = await _store.GetAsync(Guid.NewGuid().ToString("N"));
        result.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldReturnStoredItems()
    {
        var id = Guid.NewGuid().ToString("N");
        await _store.UpsertAsync(new PlatformCommandStatus
        {
            CommandId = id,
            Subsystem = "test",
            Command = "list-test",
        });

        var items = await _store.ListAsync();
        items.Should().ContainSingle(s => s.CommandId == id);
    }
}
