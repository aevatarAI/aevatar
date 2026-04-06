using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Persistent store for channel bot registrations.
/// Uses Protobuf file-based storage at ~/.aevatar/channel-registrations.bin.
/// Thread-safe via lock; suitable for low-frequency config operations.
/// </summary>
public sealed class ChannelBotRegistrationStore
{
    private readonly string _filePath;
    private readonly ILogger<ChannelBotRegistrationStore> _logger;
    private readonly object _lock = new();
    private ChannelBotRegistrationStoreState _state;

    public ChannelBotRegistrationStore(ILogger<ChannelBotRegistrationStore> logger)
    {
        _logger = logger;
        var aevatarDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aevatar");
        Directory.CreateDirectory(aevatarDir);
        _filePath = Path.Combine(aevatarDir, "channel-registrations.bin");
        _state = Load();
    }

    public ChannelBotRegistrationEntry? Get(string registrationId)
    {
        lock (_lock)
        {
            return _state.Registrations.FirstOrDefault(r => r.Id == registrationId);
        }
    }

    public IReadOnlyList<ChannelBotRegistrationEntry> List()
    {
        lock (_lock)
        {
            return _state.Registrations.ToList();
        }
    }

    public ChannelBotRegistrationEntry Register(
        string platform,
        string nyxProviderSlug,
        string nyxUserToken,
        string? verificationToken,
        string? scopeId)
    {
        var entry = new ChannelBotRegistrationEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Platform = platform,
            NyxProviderSlug = nyxProviderSlug,
            NyxUserToken = nyxUserToken,
            VerificationToken = verificationToken ?? string.Empty,
            ScopeId = scopeId ?? string.Empty,
            CreatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        lock (_lock)
        {
            _state.Registrations.Add(entry);
            Save();
        }

        _logger.LogInformation("Registered channel bot: id={Id}, platform={Platform}, slug={Slug}",
            entry.Id, platform, nyxProviderSlug);
        return entry;
    }

    public bool Delete(string registrationId)
    {
        lock (_lock)
        {
            var entry = _state.Registrations.FirstOrDefault(r => r.Id == registrationId);
            if (entry is null)
                return false;

            _state.Registrations.Remove(entry);
            Save();

            _logger.LogInformation("Deleted channel bot registration: id={Id}", registrationId);
            return true;
        }
    }

    private ChannelBotRegistrationStoreState Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var bytes = File.ReadAllBytes(_filePath);
                var state = ChannelBotRegistrationStoreState.Parser.ParseFrom(bytes);
                _logger.LogInformation("Loaded {Count} channel bot registrations from {Path}",
                    state.Registrations.Count, _filePath);
                return state;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load channel registrations from {Path}, starting fresh", _filePath);
        }

        return new ChannelBotRegistrationStoreState();
    }

    private void Save()
    {
        try
        {
            var bytes = _state.ToByteArray();
            File.WriteAllBytes(_filePath, bytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save channel registrations to {Path}", _filePath);
        }
    }
}
