using System.Text;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public interface IUserMemoryPromptContextProvider
{
    Task<string> BuildAsync(int maxChars, CancellationToken ct = default);
}

/// <summary>
/// Derives bounded prompt input from the authenticated user's projected memory.
/// The returned text is execution input, not a user-memory or transcript fact.
/// </summary>
internal sealed class UserMemoryPromptContextProvider : IUserMemoryPromptContextProvider
{
    private const string OpeningTag = "<user-memory>\n";
    private const string ClosingTag = "</user-memory>";

    private readonly IUserMemoryQueryPort? _queryPort;
    private readonly ILogger<UserMemoryPromptContextProvider> _logger;

    public UserMemoryPromptContextProvider(
        IUserMemoryQueryPort? queryPort,
        ILogger<UserMemoryPromptContextProvider> logger)
    {
        _queryPort = queryPort;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> BuildAsync(int maxChars, CancellationToken ct = default)
    {
        if (_queryPort is null || maxChars <= OpeningTag.Length + ClosingTag.Length)
            return string.Empty;

        UserMemorySnapshot snapshot;
        try
        {
            snapshot = await _queryPort.GetAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "User memory read model is unavailable; continuing without user-memory prompt context.");
            return string.Empty;
        }

        return Build(snapshot.Entries, maxChars);
    }

    internal static string Build(
        IReadOnlyList<UserMemoryEntrySnapshot> entries,
        int maxChars)
    {
        if (entries.Count == 0 || maxChars <= OpeningTag.Length + ClosingTag.Length)
            return string.Empty;

        var groups = entries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Content))
            .GroupBy(static entry => entry.Category)
            .OrderBy(static group => CategoryOrder(group.Key));

        var builder = new StringBuilder(OpeningTag);
        var appendedEntry = false;
        foreach (var group in groups)
        {
            var header = CategoryHeader(group.Key) + "\n";
            if (!CanAppend(builder, header, maxChars))
                break;

            var headerLength = builder.Length;
            builder.Append(header);
            var appendedInGroup = false;
            foreach (var entry in group.OrderByDescending(static entry => entry.UpdatedAt))
            {
                var line = $"- {entry.Content.Trim()}\n";
                if (!CanAppend(builder, line, maxChars))
                    break;

                builder.Append(line);
                appendedEntry = true;
                appendedInGroup = true;
            }

            if (!appendedInGroup)
            {
                builder.Length = headerLength;
                break;
            }

            if (CanAppend(builder, "\n", maxChars))
                builder.Append('\n');
        }

        if (!appendedEntry)
            return string.Empty;

        builder.Append(ClosingTag);
        return builder.ToString();
    }

    private static bool CanAppend(StringBuilder builder, string value, int maxChars) =>
        builder.Length + value.Length + ClosingTag.Length <= maxChars;

    private static int CategoryOrder(UserMemoryCategory category) =>
        category switch
        {
            UserMemoryCategory.Preference => 0,
            UserMemoryCategory.Instruction => 1,
            UserMemoryCategory.Context => 2,
            _ => 3,
        };

    private static string CategoryHeader(UserMemoryCategory category) =>
        category switch
        {
            UserMemoryCategory.Preference => "## Preferences",
            UserMemoryCategory.Instruction => "## Instructions",
            UserMemoryCategory.Context => "## Context",
            _ => "## Other",
        };
}
