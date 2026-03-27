using Aevatar.Studio.Infrastructure.Storage;
using FluentAssertions;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ChronoStorageMasterKeyResolverTests
{
    [Fact]
    public void ResolveMasterKey_WhenConfiguredValueMissing_ShouldCreateReusableFileBackedKey()
    {
        using var rootDirectory = new TemporaryDirectory();
        var resolver = new ChronoStorageMasterKeyResolver(rootDirectory.Path, allowKeychain: false);

        var first = resolver.ResolveMasterKey(null);
        var second = resolver.ResolveMasterKey(string.Empty);
        var keyPath = Path.Combine(rootDirectory.Path, "masterkey.bin");

        first.Should().NotBeNullOrWhiteSpace();
        second.Should().Be(first);
        File.Exists(keyPath).Should().BeTrue();
        File.ReadAllBytes(keyPath).Should().HaveCount(32);
    }

    [Fact]
    public void ResolveMasterKey_WhenConfiguredValueProvided_ShouldReturnConfiguredValue()
    {
        using var rootDirectory = new TemporaryDirectory();
        var resolver = new ChronoStorageMasterKeyResolver(rootDirectory.Path, allowKeychain: false);

        var resolved = resolver.ResolveMasterKey("explicit-master-key");

        resolved.Should().Be("explicit-master-key");
        File.Exists(Path.Combine(rootDirectory.Path, "masterkey.bin")).Should().BeFalse();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aevatar-masterkey-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
