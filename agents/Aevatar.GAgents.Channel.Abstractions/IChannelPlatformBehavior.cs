using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>Identity of one optional, narrowly scoped platform behavior.</summary>
public interface IChannelPlatformBehavior
{
    /// <summary>The canonical platform whose protocol this implementation handles.</summary>
    string Platform { get; }
}

/// <summary>Validates uniqueness and resolves optional behavior by canonical identity.</summary>
public static class ChannelPlatformBehavior
{
    /// <summary>Rejects duplicate or noncanonical registrations before processing messages.</summary>
    public static IReadOnlyList<T> Validate<T>(IEnumerable<T>? implementations) where T : IChannelPlatformBehavior
    {
        var result = (implementations ?? []).ToArray();
        foreach (var item in result)
            ChannelPlatformId.FromCanonical(item.Platform);
        var duplicate = result.GroupBy(item => item.Platform, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate {typeof(T).Name} for platform '{duplicate.Key}'.");
        return result;
    }

    /// <summary>Returns the implementation or null; absence never implies a successful no-op.</summary>
    public static T? Resolve<T>(IEnumerable<T> implementations, string platform) where T : class, IChannelPlatformBehavior
    {
        var canonical = ChannelPlatformId.FromCanonical(platform).Value;
        return implementations.SingleOrDefault(item => string.Equals(item.Platform, canonical, StringComparison.Ordinal));
    }
}
